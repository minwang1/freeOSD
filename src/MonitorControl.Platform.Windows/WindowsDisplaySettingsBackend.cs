using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;

namespace MonitorControl.Platform.Windows;

/// <summary>
/// Windows display-mode enumeration and timed-trial adapter. The coordinator owns
/// trial deadlines; this adapter keeps the exact DEVMODE snapshot needed for rollback.
/// </summary>
public sealed class WindowsDisplaySettingsBackend : IPlatformDisplaySettingsBackend
{
    private const int CurrentSettings = -1;
    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;
    private const int DispChangeBadMode = -2;
    private const int DispChangeNotUpdated = -3;
    private const int DispChangeBadFlags = -4;
    private const int DispChangeBadParam = -5;
    private const int DispChangeBadDualView = -6;
    private const string SnapshotKey = "windows.devmode.token.v2";
    private const string DisplayNameKey = "windows.deviceName";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _displayGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrialEntry> _trials = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, NativeModeSnapshot> ModeSnapshots = new(StringComparer.Ordinal);

    static WindowsDisplaySettingsBackend()
    {
        if (Marshal.SizeOf<Native.DevMode>() != 220)
            throw new TypeLoadException("The DEVMODEW interop structure must match the 220-byte Windows ABI.");
    }

    public async ValueTask<PlatformDisplayCapabilities> GetCapabilitiesAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var displayName = ResolveDisplayName(monitorId);
        return await Task.Run(() =>
        {
            var current = ReadCurrentMode(displayName, monitorId);
            _ = current;
            return new PlatformDisplayCapabilities(
                CapabilityAvailability.Supported,
                CapabilityAvailability.Supported,
                CapabilityAvailability.Supported,
                CapabilityAvailability.Unsupported,
                CapabilityAvailability.Unsupported,
                ["Windows HDR control is not implemented by this adapter."]);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DisplayMode>> GetAvailableModesAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var displayName = ResolveDisplayName(monitorId);
        var gate = _displayGates.GetOrAdd(displayName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => EnumerateModes(displayName, monitorId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<DisplayModeTrial> ApplyDisplayModeTrialAsync(
        string trialId,
        MonitorId monitorId,
        DisplayMode requestedMode,
        DisplayModeChangeKind changeKind,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(trialId);
        ArgumentNullException.ThrowIfNull(requestedMode);

        var displayName = ResolveDisplayName(monitorId);
        var gate = _displayGates.GetOrAdd(displayName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_trials.ContainsKey(trialId))
                throw InvalidState($"Refresh-rate trial '{trialId}' already exists.", monitorId);
            if (_trials.Values.Any(entry =>
                    string.Equals(entry.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Status == TrialStatus.Pending))
                throw InvalidState("A refresh-rate trial is already active for this display.", monitorId);

            var previousNative = ReadCurrentMode(displayName, monitorId);
            var requestedNative = DecodeSnapshot(requestedMode, displayName, monitorId);
            if (requestedNative.PelsWidth != requestedMode.Width ||
                requestedNative.PelsHeight != requestedMode.Height ||
                requestedNative.DisplayFrequency != requestedMode.RefreshRateHz)
                throw new BackendOperationException(BackendErrorKind.InvalidValue, "The display mode metadata does not match its native snapshot.", monitorId);
            var previousMode = ToDisplayMode(previousNative, displayName, isCurrent: true);
            var canonicalRequested = ToDisplayMode(requestedNative, displayName, isCurrent: false);
            ValidateIndependentChange(previousMode, canonicalRequested, changeKind, monitorId);
            Trace.TraceInformation($"display.mode.trial.started kind={changeKind} display={displayName} previous={previousMode.Width}x{previousMode.Height}@{previousMode.RefreshRateHz} requested={canonicalRequested.Width}x{canonicalRequested.Height}@{canonicalRequested.RefreshRateHz}");

            cancellationToken.ThrowIfCancellationRequested();
            var testResult = Native.ChangeDisplaySettingsEx(displayName, ref requestedNative, IntPtr.Zero, ChangeTest, IntPtr.Zero);
            if (testResult != DispChangeSuccessful)
            {
                Trace.TraceWarning($"display.mode.test.failed kind={changeKind} display={displayName} result={testResult}");
                throw DisplayChangeError(testResult, "Windows rejected the requested display mode during validation.", monitorId);
            }
            Trace.TraceInformation($"display.mode.test.succeeded kind={changeKind} display={displayName}");

            // Store rollback data before the side effect so even an uncertain native
            // call outcome can still be recovered through the coordinator's trial id.
            var entry = new TrialEntry(displayName, previousNative);
            if (!_trials.TryAdd(trialId, entry))
                throw InvalidState($"Refresh-rate trial '{trialId}' already exists.", monitorId);

            var applyResult = Native.ChangeDisplaySettingsEx(displayName, ref requestedNative, IntPtr.Zero, 0, IntPtr.Zero);
            if (applyResult != DispChangeSuccessful)
            {
                _trials.TryRemove(trialId, out _);
                Trace.TraceWarning($"display.mode.apply.failed kind={changeKind} display={displayName} result={applyResult}");
                throw DisplayChangeError(applyResult, "Windows could not apply the requested display mode.", monitorId);
            }

            Trace.TraceInformation($"display.mode.apply.api_succeeded kind={changeKind} display={displayName}");
            var actualNative = ReadCurrentMode(displayName, monitorId);
            var actualMode = ToDisplayMode(actualNative, displayName, isCurrent: true);
            var actualMatchesRequested = MatchesAppliedMode(actualMode, canonicalRequested);
            Trace.TraceInformation($"display.mode.apply.readback kind={changeKind} display={displayName} requested={canonicalRequested.Width}x{canonicalRequested.Height}@{canonicalRequested.RefreshRateHz} actual={actualMode.Width}x{actualMode.Height}@{actualMode.RefreshRateHz} matches={actualMatchesRequested}");
            if (!actualMatchesRequested)
            {
                var rollbackMode = previousNative;
                var rollbackResult = Native.ChangeDisplaySettingsEx(displayName, ref rollbackMode, IntPtr.Zero, 0, IntPtr.Zero);
                Trace.TraceWarning($"display.mode.apply.mismatch.rollback kind={changeKind} display={displayName} result={rollbackResult}");
                if (rollbackResult == DispChangeSuccessful)
                {
                    _trials.TryRemove(trialId, out _);
                    throw new BackendOperationException(BackendErrorKind.WriteFailure,
                        "Windows reported success, but the active display mode did not match the requested mode; the previous mode was restored.", monitorId);
                }

                throw RestoreError(rollbackResult, displayName);
            }

            canonicalRequested = actualMode;
            return new DisplayModeTrial(trialId, monitorId, previousMode, canonicalRequested, DateTimeOffset.UtcNow, changeKind);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ConfirmDisplayModeTrialAsync(
        string trialId,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetTrial(trialId);
        var gate = _displayGates.GetOrAdd(entry.DisplayName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry = GetTrial(trialId);
            lock (entry.Sync)
            {
                if (entry.Status == TrialStatus.Confirmed) return;
                if (entry.Status == TrialStatus.Restored)
                    throw InvalidState($"Trial '{trialId}' has already been restored.");
                entry.Status = TrialStatus.Confirmed;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask RestoreDisplayModeTrialAsync(string trialId)
    {
        EnsureWindows();
        var entry = GetTrial(trialId);
        var gate = _displayGates.GetOrAdd(entry.DisplayName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry = GetTrial(trialId);
            lock (entry.Sync)
            {
                if (entry.Status == TrialStatus.Restored) return;
                if (entry.Status == TrialStatus.Confirmed)
                    throw InvalidState($"Trial '{trialId}' has already been confirmed.");
            }

            var previous = entry.PreviousMode;
            var result = await Task.Run(() => Native.ChangeDisplaySettingsEx(
                entry.DisplayName, ref previous, IntPtr.Zero, 0, IntPtr.Zero)).ConfigureAwait(false);
            if (result != DispChangeSuccessful)
            {
                Trace.TraceWarning($"display.mode.restore.failed display={entry.DisplayName} result={result}");
                throw RestoreError(result, entry.DisplayName);
            }

            lock (entry.Sync) entry.Status = TrialStatus.Restored;
            Trace.TraceInformation($"display.mode.restore.succeeded display={entry.DisplayName} mode={entry.PreviousMode.PelsWidth}x{entry.PreviousMode.PelsHeight}@{entry.PreviousMode.DisplayFrequency}");
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask<HdrState> GetHdrStateAsync(MonitorId monitorId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolveDisplayName(monitorId);
        return ValueTask.FromResult(new HdrState(IsSupported: false, IsEnabled: false));
    }

    public ValueTask SetHdrStateAsync(MonitorId monitorId, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolveDisplayName(monitorId);
        throw new BackendOperationException(BackendErrorKind.Unsupported, "HDR control is not implemented by this adapter.", monitorId);
    }

    private static IReadOnlyList<DisplayMode> EnumerateModes(
        string displayName,
        MonitorId monitorId,
        CancellationToken cancellationToken)
    {
        var current = ReadCurrentMode(displayName, monitorId);
        foreach (var snapshot in ModeSnapshots.Where(pair =>
                     string.Equals(pair.Value.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            ModeSnapshots.TryRemove(snapshot.Key, out _);
        }
        var modes = new Dictionary<string, DisplayMode>(StringComparer.Ordinal);
        modes[ModeKey(current)] = ToDisplayMode(current, displayName, isCurrent: true);

        for (var index = 0; index < 4096; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mode = CreateDevMode();
            if (!Native.EnumDisplaySettingsEx(displayName, index, ref mode, 0)) break;
            var key = ModeKey(mode);
            if (!modes.ContainsKey(key))
                modes.Add(key, ToDisplayMode(mode, displayName, isCurrent: key == ModeKey(current)));
        }

        Trace.TraceInformation($"display.mode.enumeration.completed display={displayName} count={modes.Count} current={current.PelsWidth}x{current.PelsHeight}@{current.DisplayFrequency}");
        return modes.Values
            .OrderBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .ThenBy(mode => mode.RefreshRateNumerator / (double)Math.Max(1, mode.RefreshRateDenominator))
            .ThenBy(mode => mode.BitsPerPixel)
            .ToArray();
    }

    private static Native.DevMode ReadCurrentMode(string displayName, MonitorId? monitorId = null)
    {
        var mode = CreateDevMode();
        if (!Native.EnumDisplaySettingsEx(displayName, CurrentSettings, ref mode, 0))
            throw new BackendOperationException(
                BackendErrorKind.Unavailable,
                $"Could not read the current display mode for '{displayName}'.",
                monitorId,
                platformCode: Marshal.GetLastWin32Error().ToString(),
                isTransient: true);
        return mode;
    }

    private static Native.DevMode CreateDevMode() => new()
    {
        Size = (ushort)Marshal.SizeOf<Native.DevMode>(),
        DeviceName = string.Empty,
        FormName = string.Empty
    };

    private static DisplayMode ToDisplayMode(Native.DevMode mode, string displayName, bool isCurrent)
    {
        var snapshotToken = Guid.NewGuid().ToString("N");
        ModeSnapshots[snapshotToken] = new NativeModeSnapshot(displayName, mode);
        return new DisplayMode(
            ModeKey(mode),
            checked((int)mode.PelsWidth),
            checked((int)mode.PelsHeight),
            mode.BitsPerPel,
            mode.DisplayFrequency,
            1,
            checked((int)mode.DisplayFlags),
            checked((int)mode.DisplayOrientation),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SnapshotKey] = snapshotToken,
                [DisplayNameKey] = displayName
            },
            isCurrent);
    }

    private static Native.DevMode DecodeSnapshot(DisplayMode mode, string expectedDisplayName, MonitorId monitorId)
    {
        if (!mode.PlatformData.TryGetValue(SnapshotKey, out var snapshotToken) ||
            !mode.PlatformData.TryGetValue(DisplayNameKey, out var displayName) ||
            !string.Equals(displayName, expectedDisplayName, StringComparison.OrdinalIgnoreCase))
            throw new BackendOperationException(BackendErrorKind.InvalidValue, "The selected display mode is not a mode enumerated for this display.", monitorId);

        if (!ModeSnapshots.TryGetValue(snapshotToken, out var snapshot) ||
            !string.Equals(snapshot.DisplayName, expectedDisplayName, StringComparison.OrdinalIgnoreCase))
            throw new BackendOperationException(BackendErrorKind.InvalidValue, "The selected display mode snapshot is invalid or expired; reload the available modes.", monitorId);

        var result = snapshot.Mode;
        // DEVMODE.dmDeviceName identifies the display driver's DLL, not the
        // EnumDisplayDevices path (for example, \\.\DISPLAY1). The opaque token
        // above binds this snapshot to the display path separately.
        if (result.PelsWidth == 0 || result.PelsHeight == 0 || result.DisplayFrequency == 0 ||
            ModeKey(result) != mode.ModeId)
            throw new BackendOperationException(BackendErrorKind.InvalidValue, "The selected display mode snapshot does not match its mode metadata.", monitorId);

        return result;
    }

    private static string ModeKey(Native.DevMode mode) =>
        $"{mode.DeviceName}|{mode.PelsWidth}x{mode.PelsHeight}|{mode.DisplayFrequency}/{1}|{mode.BitsPerPel}|{mode.DisplayOrientation}|{mode.DisplayFixedOutput}|{mode.DisplayFlags}";

    private static bool MatchesAppliedMode(DisplayMode actual, DisplayMode requested)
    {
        var actualRate = actual.RefreshRateNumerator / (double)Math.Max(1, actual.RefreshRateDenominator);
        var requestedRate = requested.RefreshRateNumerator / (double)Math.Max(1, requested.RefreshRateDenominator);
        return actual.Width == requested.Width && actual.Height == requested.Height &&
               actual.BitsPerPixel == requested.BitsPerPixel &&
               actual.Orientation == requested.Orientation &&
               actual.ScanLineOrdering == requested.ScanLineOrdering &&
               Math.Abs(actualRate - requestedRate) <= 1.0;
    }

    private static void ValidateIndependentChange(
        DisplayMode previous,
        DisplayMode requested,
        DisplayModeChangeKind changeKind,
        MonitorId monitorId)
    {
        var sameRate = (ulong)previous.RefreshRateNumerator * requested.RefreshRateDenominator ==
                       (ulong)requested.RefreshRateNumerator * previous.RefreshRateDenominator;
        var sameResolution = previous.Width == requested.Width && previous.Height == requested.Height;
        var sameOtherFields = previous.BitsPerPixel == requested.BitsPerPixel &&
                              previous.ScanLineOrdering == requested.ScanLineOrdering &&
                              previous.Orientation == requested.Orientation;

        var changesOnlyRequestedSetting = changeKind switch
        {
            DisplayModeChangeKind.Resolution => !sameResolution && sameRate && sameOtherFields,
            DisplayModeChangeKind.RefreshRate => sameResolution && !sameRate && sameOtherFields,
            _ => false
        };

        if (!changesOnlyRequestedSetting)
        {
            throw new BackendOperationException(
                BackendErrorKind.InvalidValue,
                changeKind == DisplayModeChangeKind.Resolution
                    ? "The selected mode would also change refresh rate or another display setting."
                    : "The selected mode would also change resolution or another display setting.",
                monitorId);
        }
    }

    private static string ResolveDisplayName(MonitorId monitorId)
    {
        var value = monitorId.Value;
        var separator = value.IndexOf('|');
        var displayName = separator >= 0 ? value[..separator] : value;
        if (displayName.StartsWith(@"\\.", StringComparison.OrdinalIgnoreCase) &&
            displayName.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase))
            return displayName;
        throw new BackendOperationException(BackendErrorKind.Disconnected, "The monitor ID does not contain a valid Windows display device name.", monitorId);
    }

    private TrialEntry GetTrial(string trialId)
    {
        if (!_trials.TryGetValue(trialId, out var entry))
            throw InvalidState($"Refresh-rate trial '{trialId}' is unknown or no longer active.");
        return entry;
    }

    private static BackendOperationException InvalidState(string message, MonitorId? monitorId = null) =>
        new(BackendErrorKind.InvalidState, message, monitorId);

    private static BackendOperationException DisplayChangeError(int result, string message, MonitorId? monitorId = null) =>
        new(result is DispChangeBadMode or DispChangeBadParam
                ? BackendErrorKind.InvalidValue
                : result == DispChangeRestart
                    ? BackendErrorKind.Unavailable
                    : BackendErrorKind.WriteFailure,
            $"{message} {DescribeDisplayChangeResult(result)}",
            monitorId,
            platformCode: result.ToString());

    private static BackendOperationException RestoreError(int result, string displayName) =>
        new(BackendErrorKind.RestoreFailure,
            $"Could not restore the previous display mode for '{displayName}'. {DescribeDisplayChangeResult(result)}",
            platformCode: result.ToString(),
            isTransient: true);

    private static string DescribeDisplayChangeResult(int result) => result switch
    {
        DispChangeRestart => "Windows reported that the change requires a restart.",
        DispChangeBadMode => "The requested mode is unsupported.",
        DispChangeNotUpdated => "Windows could not update the display settings.",
        DispChangeBadFlags => "Windows rejected the change flags.",
        DispChangeBadParam => "Windows rejected a display mode parameter.",
        DispChangeBadDualView => "The change is not supported in this DualView configuration.",
        _ => $"DisplayChange result: {result}."
    };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new BackendOperationException(BackendErrorKind.Unsupported, "Windows display mode APIs are available only on Windows.");
    }

    private sealed class TrialEntry(string displayName, Native.DevMode previousMode)
    {
        public object Sync { get; } = new();
        public string DisplayName { get; } = displayName;
        public Native.DevMode PreviousMode { get; } = previousMode;
        public TrialStatus Status { get; set; } = TrialStatus.Pending;
    }

    private sealed record NativeModeSnapshot(string DisplayName, Native.DevMode Mode);

    private enum TrialStatus { Pending, Confirmed, Restored }

    private const uint ChangeTest = 0x00000002;

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 2)]
        internal struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion;
            public ushort DriverVersion;
            public ushort Size;
            public ushort DriverExtra;
            public uint Fields;
            public DevModeUnion Display;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TrueTypeOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel;
            public uint PelsWidth;
            public uint PelsHeight;
            public uint DisplayFlags;
            public uint DisplayFrequency;
            public uint IcmMethod;
            public uint IcmIntent;
            public uint MediaType;
            public uint DitherType;
            public uint Reserved1;
            public uint Reserved2;
            public uint PanningWidth;
            public uint PanningHeight;

            public uint DisplayOrientation { readonly get => Display.DisplayOrientation; set => Display.DisplayOrientation = value; }
            public uint DisplayFixedOutput { readonly get => Display.DisplayFixedOutput; set => Display.DisplayFixedOutput = value; }
        }

        // DEVMODEW overlays printer and display mode data in a 16-byte union. Keeping
        // this nested explicit struct blittable avoids invalid managed string overlaps.
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct DevModeUnion
        {
            [FieldOffset(0)] public short Orientation;
            [FieldOffset(2)] public short PaperSize;
            [FieldOffset(4)] public short PaperLength;
            [FieldOffset(6)] public short PaperWidth;
            [FieldOffset(8)] public short Scale;
            [FieldOffset(10)] public short Copies;
            [FieldOffset(12)] public short DefaultSource;
            [FieldOffset(14)] public short PrintQuality;
            [FieldOffset(0)] public int PositionX;
            [FieldOffset(4)] public int PositionY;
            [FieldOffset(8)] public uint DisplayOrientation;
            [FieldOffset(12)] public uint DisplayFixedOutput;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DevMode mode, uint flags);

        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, IntPtr window, uint flags, IntPtr parameter);
    }
}
