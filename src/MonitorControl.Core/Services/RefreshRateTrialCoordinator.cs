using System.Collections.Concurrent;
using System.Diagnostics;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;

namespace MonitorControl.Core.Services;

public enum RefreshRateTrialStatus
{
    Pending,
    Confirming,
    Confirmed,
    Restoring,
    Restored,
    RestoreFailed
}

/// <summary>Observable state for a refresh-rate trial, including its rollback deadline.</summary>
public sealed record RefreshRateTrialState(
    DisplayModeTrial Trial,
    DateTimeOffset Deadline,
    RefreshRateTrialStatus Status,
    int RestoreAttempts = 0,
    string? LastError = null);

public sealed class RefreshRateTrialStateEventArgs(RefreshRateTrialState state) : EventArgs
{
    public RefreshRateTrialState State { get; } = state;
}

/// <summary>
/// Owns refresh-rate trial deadlines independently of any window. A user cancel and a
/// timeout both make the same terminal restore decision. Confirm and restore are
/// serialized per trial; restoration never receives a caller cancellation token.
/// DisposeAsync stops deadlines and attempts to restore every still-unconfirmed trial.
/// </summary>
public sealed class RefreshRateTrialCoordinator : IAsyncDisposable
{
    private readonly IPlatformDisplaySettingsBackend _backend;
    private readonly ConcurrentDictionary<string, TrialEntry> _trials = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    public RefreshRateTrialCoordinator(IPlatformDisplaySettingsBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>Raised on the coordinator's worker thread; subscribers marshal to their UI.</summary>
    public event EventHandler<RefreshRateTrialStateEventArgs>? StateChanged;

    /// <summary>
    /// Applies a mode trial and starts its deadline after the backend confirms application.
    /// Cancellation applies only before/during the backend's apply operation; after a
    /// trial is returned, its restore timer is owned by this coordinator.
    /// </summary>
    public async ValueTask<RefreshRateTrialState> StartTrialAsync(
        MonitorId monitorId,
        DisplayMode requestedMode,
        TimeSpan confirmationWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedMode);
        if (confirmationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationWindow), "The confirmation window must be positive.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var trialId = CreateUniqueTrialId();
            var trial = await _backend.ApplyRefreshRateTrialAsync(
                trialId, monitorId, requestedMode, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(trial.TrialId, trialId, StringComparison.Ordinal))
            {
                try
                {
                    // The adapter contract requires the supplied ID to be its transaction
                    // key, so this rollback target remains unambiguous even on bad output.
                    await _backend.RestoreRefreshRateTrialAsync(trialId).ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    throw new BackendOperationException(
                        BackendErrorKind.RestoreFailure,
                        $"The backend returned a mismatched trial id and rollback failed: {restoreError.Message}",
                        monitorId,
                        platformCode: null,
                        innerException: restoreError);
                }

                throw new BackendOperationException(
                    BackendErrorKind.InvalidState,
                    $"The backend returned trial id '{trial.TrialId}' instead of the requested id '{trialId}'.",
                    monitorId);
            }
            var state = new RefreshRateTrialState(
                trial,
                DateTimeOffset.UtcNow + confirmationWindow,
                RefreshRateTrialStatus.Pending);
            var entry = new TrialEntry(state, confirmationWindow);

            if (!_trials.TryAdd(trial.TrialId, entry))
            {
                // IDs are allocated here and checked while starts are serialized; reaching
                // this branch indicates a coordinator invariant violation. The requested
                // backend key is still known, so rollback does not target an ambiguous ID.
                await _backend.RestoreRefreshRateTrialAsync(trialId).ConfigureAwait(false);
                throw new InvalidOperationException($"Coordinator generated duplicate refresh-rate trial id '{trialId}'.");
            }

            Publish(entry.State);
            entry.DeadlineTask = WatchDeadlineAsync(entry, confirmationWindow);
            return entry.State;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public RefreshRateTrialState? GetState(string trialId) =>
        _trials.TryGetValue(trialId, out var entry) ? Volatile.Read(ref entry.State) : null;

    public IReadOnlyList<RefreshRateTrialState> GetStates() =>
        _trials.Values.Select(entry => Volatile.Read(ref entry.State)).OrderBy(state => state.Deadline).ToArray();

    /// <summary>
    /// Confirms before the deadline. If the deadline has already elapsed, restoration
    /// wins. Once this method acquires the per-trial gate, caller cancellation cannot
    /// interrupt an in-progress backend decision.
    /// </summary>
    public async ValueTask<RefreshRateTrialState> ConfirmAsync(
        string trialId,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(trialId);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return await RestoreUnderGateAsync(entry).ConfigureAwait(false);
            }

            if (entry.State.Status == RefreshRateTrialStatus.Confirmed)
            {
                return entry.State;
            }
            if (entry.State.Status == RefreshRateTrialStatus.Restored ||
                entry.State.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                throw InvalidTransition(trialId, entry.State.Status, "confirm");
            }
            if (Stopwatch.GetTimestamp() >= entry.MonotonicDeadline)
            {
                return await RestoreUnderGateAsync(entry).ConfigureAwait(false);
            }

            SetState(entry, entry.State with { Status = RefreshRateTrialStatus.Confirming, LastError = null });
            try
            {
                await _backend.ConfirmRefreshRateTrialAsync(trialId, cancellationToken).ConfigureAwait(false);
                SetState(entry, entry.State with { Status = RefreshRateTrialStatus.Confirmed, LastError = null });
                entry.TimerCancellation.Cancel();
                return entry.State;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The backend contract guarantees cancellation only before commit wins.
                // Restore the pending trial rather than leave a timerless uncertain state.
                SetState(entry, entry.State with { Status = RefreshRateTrialStatus.Pending });
                return await RestoreUnderGateAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Keep the trial pending so the deadline remains armed. The timeout will
                // attempt rollback; the caller receives the typed backend failure.
                SetState(entry, entry.State with
                {
                    Status = RefreshRateTrialStatus.Pending,
                    LastError = exception.Message
                });
                throw;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>User cancellation selects restoration; it is never itself cancelable.</summary>
    public async ValueTask<RefreshRateTrialState> CancelAsync(string trialId)
    {
        var entry = GetEntry(trialId);
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.State.Status == RefreshRateTrialStatus.Confirmed)
            {
                throw InvalidTransition(trialId, entry.State.Status, "restore");
            }
            return await RestoreUnderGateAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Retries a restore that previously failed; the restore call is non-cancelable.</summary>
    public async ValueTask<RefreshRateTrialState> RetryRestoreAsync(string trialId)
    {
        var entry = GetEntry(trialId);
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.State.Status == RefreshRateTrialStatus.Restored)
            {
                return entry.State;
            }
            if (entry.State.Status == RefreshRateTrialStatus.Confirmed)
            {
                throw InvalidTransition(trialId, entry.State.Status, "restore");
            }
            if (entry.State.Status != RefreshRateTrialStatus.RestoreFailed)
            {
                throw InvalidTransition(trialId, entry.State.Status, "retry restore");
            }
            return await RestoreUnderGateAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        TrialEntry[]? entries = null;
        Task? priorDispose = null;
        try
        {
            if (_disposed)
            {
                priorDispose = _disposeCompletion?.Task;
            }
            else
            {
                _disposed = true;
                _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                entries = _trials.Values.ToArray();
                foreach (var entry in entries)
                {
                    entry.TimerCancellation.Cancel();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (priorDispose is not null)
        {
            await priorDispose.ConfigureAwait(false);
            return;
        }
        if (entries is null)
        {
            return;
        }

        // Timers are canceled first, then each entry is resolved through the same gate
        // used by confirm/cancel. Any still-active trial receives one shutdown restore
        // attempt; failed states remain visible and can be retried explicitly.
        try
        {
            foreach (var entry in entries)
            {
                if (entry.DeadlineTask is not null)
                {
                    try { await entry.DeadlineTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                await entry.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (entry.State.Status is RefreshRateTrialStatus.Pending or RefreshRateTrialStatus.RestoreFailed)
                    {
                        await RestoreUnderGateAsync(entry).ConfigureAwait(false);
                    }
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            _disposeCompletion!.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion!.TrySetException(exception);
            throw;
        }
    }

    private async Task WatchDeadlineAsync(TrialEntry entry, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, entry.TimerCancellation.Token).ConfigureAwait(false);
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (entry.State.Status == RefreshRateTrialStatus.Pending)
                {
                    await RestoreUnderGateAsync(entry).ConfigureAwait(false);
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (entry.TimerCancellation.IsCancellationRequested)
        {
            // Confirm or shutdown owns the terminal decision.
        }
    }

    private async ValueTask<RefreshRateTrialState> RestoreUnderGateAsync(TrialEntry entry)
    {
        if (entry.State.Status == RefreshRateTrialStatus.Restored)
        {
            return entry.State;
        }
        if (entry.State.Status == RefreshRateTrialStatus.Confirmed)
        {
            throw InvalidTransition(entry.State.Trial.TrialId, entry.State.Status, "restore");
        }

        SetState(entry, entry.State with
        {
            Status = RefreshRateTrialStatus.Restoring,
            RestoreAttempts = entry.State.RestoreAttempts + 1,
            LastError = null
        });
        try
        {
            // Intentionally no cancellation token: recovery must finish once started.
            await _backend.RestoreRefreshRateTrialAsync(entry.State.Trial.TrialId).ConfigureAwait(false);
            SetState(entry, entry.State with { Status = RefreshRateTrialStatus.Restored, LastError = null });
            entry.TimerCancellation.Cancel();
        }
        catch (Exception exception)
        {
            SetState(entry, entry.State with
            {
                Status = RefreshRateTrialStatus.RestoreFailed,
                LastError = exception.Message
            });
        }
        return entry.State;
    }

    private TrialEntry GetEntry(string trialId)
    {
        ThrowIfDisposedUnlessRetry(trialId);
        return _trials.TryGetValue(trialId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Refresh-rate trial '{trialId}' was not found.");
    }

    private string CreateUniqueTrialId()
    {
        string trialId;
        do
        {
            trialId = Guid.NewGuid().ToString("N");
        } while (_trials.ContainsKey(trialId));

        return trialId;
    }

    private void ThrowIfDisposedUnlessRetry(string trialId)
    {
        if (_disposed && (!_trials.TryGetValue(trialId, out var entry) ||
                          entry.State.Status != RefreshRateTrialStatus.RestoreFailed))
        {
            throw new ObjectDisposedException(nameof(RefreshRateTrialCoordinator));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RefreshRateTrialCoordinator));
        }
    }

    private static BackendOperationException InvalidTransition(string trialId, RefreshRateTrialStatus status, string action) =>
        new(BackendErrorKind.InvalidState,
            $"Cannot {action} refresh-rate trial '{trialId}' while it is {status}.");

    private void SetState(TrialEntry entry, RefreshRateTrialState state)
    {
        Volatile.Write(ref entry.State, state);
        Publish(state);
    }

    private void Publish(RefreshRateTrialState state)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        var args = new RefreshRateTrialStateEventArgs(state);
        foreach (EventHandler<RefreshRateTrialStateEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { /* A subscriber must not disrupt the display recovery path. */ }
        }
    }

    private sealed class TrialEntry(RefreshRateTrialState initialState, TimeSpan confirmationWindow)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource TimerCancellation { get; } = new();
        // Use a monotonic clock for decisions; the UTC value is only for UI display.
        public long MonotonicDeadline { get; } = Stopwatch.GetTimestamp() +
            (long)(confirmationWindow.TotalSeconds * Stopwatch.Frequency);
        public RefreshRateTrialState State = initialState;
        public Task? DeadlineTask { get; set; }
    }
}
