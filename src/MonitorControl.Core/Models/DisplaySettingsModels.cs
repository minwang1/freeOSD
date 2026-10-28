namespace MonitorControl.Core.Models;

/// <summary>
/// A display mode snapshot. The standard fields are useful to the UI; PlatformData
/// preserves adapter-specific fields required to restore the exact mode. The adapter
/// owns the format and must reject snapshots it cannot safely restore.
/// </summary>
public sealed record DisplayMode(
    string ModeId,
    int Width,
    int Height,
    uint BitsPerPixel,
    uint RefreshRateNumerator,
    uint RefreshRateDenominator,
    int ScanLineOrdering,
    int Orientation,
    IReadOnlyDictionary<string, string> PlatformData,
    bool IsCurrent = false)
{
    public int RefreshRateHz => RefreshRateDenominator == 0
        ? 0
        : (int)Math.Round((double)RefreshRateNumerator / RefreshRateDenominator);
}

public sealed record HdrState(bool IsSupported, bool IsEnabled);

public enum CapabilityAvailability
{
    /// <summary>The backend can perform the operation for this monitor now.</summary>
    Supported,
    /// <summary>The monitor/platform definitively does not implement the operation.</summary>
    Unsupported,
    /// <summary>The capability may exist, but cannot currently be queried or used.</summary>
    Unavailable,
    /// <summary>The backend has not determined support.</summary>
    Unknown
}

/// <summary>Per-monitor capabilities supplied by the platform display backend.</summary>
public sealed record PlatformDisplayCapabilities(
    CapabilityAvailability EnumerateRefreshRates,
    CapabilityAvailability SetRefreshRate,
    CapabilityAvailability ReadHdr,
    CapabilityAvailability SetHdr,
    IReadOnlyList<string>? Limitations = null);

/// <summary>
/// The full mode trial transaction. Both mode snapshots are retained so a caller can
/// recover after navigation/window closure. TrialId is allocated by the coordinator
/// before apply and remains valid until confirmed or restored. The adapter must retain
/// it as the transaction key. The application-level coordinator owns the deadline.
/// </summary>
public sealed record DisplayModeTrial(
    string TrialId,
    MonitorId MonitorId,
    DisplayMode PreviousMode,
    DisplayMode RequestedMode,
    DateTimeOffset StartedAt);

public sealed record HdrChangeResult(HdrState PreviousState, HdrState RequestedState);
