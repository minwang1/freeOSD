using System.Collections.Concurrent;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Core.Services;

namespace MonitorControl.Core.Tests;

public static class RefreshRateTrialCoordinatorTests
{
    private static readonly MonitorId Monitor = new("test-monitor");

    public static async Task ConfirmPreventsDeadlineRestore()
    {
        var backend = new FakeDisplayBackend();
        await using var coordinator = new RefreshRateTrialCoordinator(backend);

        var trial = await coordinator.StartTrialAsync(Monitor, CreateMode(144), TimeSpan.FromMilliseconds(250));
        var confirmed = await coordinator.ConfirmAsync(trial.Trial.TrialId);
        await Task.Delay(350);

        Check.Equal(RefreshRateTrialStatus.Confirmed, confirmed.Status);
        Check.Equal(RefreshRateTrialStatus.Confirmed, coordinator.GetState(trial.Trial.TrialId)?.Status);
        Check.Equal(0, backend.RestoreCount);
    }

    public static async Task DeadlineExpiryRestoresPreviousMode()
    {
        var backend = new FakeDisplayBackend();
        await using var coordinator = new RefreshRateTrialCoordinator(backend);

        var trial = await coordinator.StartTrialAsync(Monitor, CreateMode(144), TimeSpan.FromMilliseconds(60));
        var finalState = await WaitForStateAsync(coordinator, trial.Trial.TrialId, RefreshRateTrialStatus.Restored);

        Check.Equal(RefreshRateTrialStatus.Restored, finalState.Status);
        Check.Equal(1, backend.RestoreCount);
        Check.Equal(RefreshRateTrialStatus.Restored, backend.GetTerminalState(trial.Trial.TrialId));
    }

    public static async Task UserCancelRestoresPreviousMode()
    {
        var backend = new FakeDisplayBackend();
        await using var coordinator = new RefreshRateTrialCoordinator(backend);

        var trial = await coordinator.StartTrialAsync(Monitor, CreateMode(144), TimeSpan.FromSeconds(5));
        var finalState = await coordinator.CancelAsync(trial.Trial.TrialId);

        Check.Equal(RefreshRateTrialStatus.Restored, finalState.Status);
        Check.Equal(1, backend.RestoreCount);
    }

    public static async Task RestoreFailureIsObservableAndCanBeRetried()
    {
        var backend = new FakeDisplayBackend { FailNextRestores = 1 };
        await using var coordinator = new RefreshRateTrialCoordinator(backend);
        var trial = await coordinator.StartTrialAsync(Monitor, CreateMode(144), TimeSpan.FromSeconds(5));

        var failed = await coordinator.CancelAsync(trial.Trial.TrialId);

        Check.Equal(RefreshRateTrialStatus.RestoreFailed, failed.Status);
        Check.Equal(1, failed.RestoreAttempts);
        Check.Equal("simulated restore failure", failed.LastError);

        var restored = await coordinator.RetryRestoreAsync(trial.Trial.TrialId);

        Check.Equal(RefreshRateTrialStatus.Restored, restored.Status);
        Check.Equal(2, restored.RestoreAttempts);
        Check.Equal(2, backend.RestoreCount);
    }

    public static async Task DisposalRestoresActiveTrial()
    {
        var backend = new FakeDisplayBackend();
        var coordinator = new RefreshRateTrialCoordinator(backend);
        var trial = await coordinator.StartTrialAsync(Monitor, CreateMode(144), TimeSpan.FromMinutes(1));

        await coordinator.DisposeAsync();

        Check.Equal(RefreshRateTrialStatus.Restored, coordinator.GetState(trial.Trial.TrialId)?.Status);
        Check.Equal(1, backend.RestoreCount);
    }

    private static DisplayMode CreateMode(int rateHz) => new(
        $"mode-{rateHz}",
        2560,
        1440,
        32,
        (uint)rateHz,
        1,
        1,
        0,
        new Dictionary<string, string> { ["native-token"] = $"opaque-{rateHz}" });

    private static async Task<RefreshRateTrialState> WaitForStateAsync(
        RefreshRateTrialCoordinator coordinator,
        string trialId,
        RefreshRateTrialStatus expected)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            var state = coordinator.GetState(trialId);
            if (state?.Status == expected)
            {
                return state;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException($"Trial did not reach {expected} before timeout.");
    }

    private sealed class FakeDisplayBackend : IPlatformDisplaySettingsBackend
    {
        private readonly ConcurrentDictionary<string, DisplayModeTrial> _trials = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RefreshRateTrialStatus> _terminalStates = new(StringComparer.Ordinal);
        private int _restoreCount;
        private int _failNextRestores;

        public int RestoreCount => Volatile.Read(ref _restoreCount);

        public int FailNextRestores
        {
            get => Volatile.Read(ref _failNextRestores);
            set => Volatile.Write(ref _failNextRestores, value);
        }

        public ValueTask<PlatformDisplayCapabilities> GetCapabilitiesAsync(
            MonitorId monitorId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlatformDisplayCapabilities(
                CapabilityAvailability.Supported,
                CapabilityAvailability.Supported,
                CapabilityAvailability.Unsupported,
                CapabilityAvailability.Unsupported));

        public ValueTask<IReadOnlyList<DisplayMode>> GetAvailableModesAsync(
            MonitorId monitorId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DisplayMode>>(new[] { CreateMode(60), CreateMode(144) });

        public ValueTask<DisplayModeTrial> ApplyRefreshRateTrialAsync(
            string trialId,
            MonitorId monitorId,
            DisplayMode requestedMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousMode = CreateMode(60) with { IsCurrent = true };
            var requested = requestedMode with { IsCurrent = true };
            var trial = new DisplayModeTrial(trialId, monitorId, previousMode, requested, DateTimeOffset.UtcNow);
            if (!_trials.TryAdd(trialId, trial))
            {
                throw new InvalidOperationException("Duplicate test trial id.");
            }
            return ValueTask.FromResult(trial);
        }

        public ValueTask ConfirmRefreshRateTrialAsync(
            string trialId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_trials.ContainsKey(trialId))
            {
                throw new KeyNotFoundException(trialId);
            }
            if (!_terminalStates.TryAdd(trialId, RefreshRateTrialStatus.Confirmed) &&
                _terminalStates[trialId] != RefreshRateTrialStatus.Confirmed)
            {
                throw new BackendOperationException(BackendErrorKind.InvalidState, "Opposite terminal decision already won.");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreRefreshRateTrialAsync(string trialId)
        {
            Interlocked.Increment(ref _restoreCount);
            if (TryConsumeRestoreFailure())
            {
                throw new BackendOperationException(BackendErrorKind.RestoreFailure, "simulated restore failure");
            }

            if (!_trials.ContainsKey(trialId))
            {
                throw new KeyNotFoundException(trialId);
            }
            if (!_terminalStates.TryAdd(trialId, RefreshRateTrialStatus.Restored) &&
                _terminalStates[trialId] == RefreshRateTrialStatus.Confirmed)
            {
                throw new BackendOperationException(BackendErrorKind.InvalidState, "Trial was confirmed.");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<HdrState> GetHdrStateAsync(
            MonitorId monitorId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HdrState(false, false));

        public ValueTask SetHdrStateAsync(
            MonitorId monitorId,
            bool enabled,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public RefreshRateTrialStatus? GetTerminalState(string trialId) =>
            _terminalStates.TryGetValue(trialId, out var state) ? state : null;

        private bool TryConsumeRestoreFailure()
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _failNextRestores);
                if (remaining <= 0) return false;
                if (Interlocked.CompareExchange(ref _failNextRestores, remaining - 1, remaining) == remaining)
                {
                    return true;
                }
            }
        }
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
        }
    }
}
