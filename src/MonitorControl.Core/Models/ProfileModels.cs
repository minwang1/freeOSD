namespace MonitorControl.Core.Models;

/// <summary>Presentation metadata for a validated mapping; it never stores an applied value.</summary>
public sealed record IdentifiedControlMetadata(
    string ControlId,
    string DisplayName,
    byte VcpCode,
    IReadOnlyDictionary<uint, string>? ValueLabels = null);

/// <summary>
/// Portable profile DTO. VcpValues is the only canonical source of values written to
/// VCP codes. IdentifiedControls contains labels and mappings only, never duplicate
/// effective values. Null optional platform values mean that the profile does not
/// request that setting; they are not equivalent to HDR off or an unspecified rate.
/// Legacy Python profile JSON (name-keyed records containing monitor, values with
/// hexadecimal code keys, and optional windows_settings) must be normalized by an
/// adapter/migration layer before constructing this model.
/// </summary>
public sealed record MonitorProfile(
    int SchemaVersion,
    string Name,
    DateTimeOffset CreatedAt,
    string? MonitorIdentity,
    IReadOnlyDictionary<byte, uint> VcpValues,
    IReadOnlyDictionary<string, IdentifiedControlMetadata> IdentifiedControls,
    int? RefreshRateHz = null,
    bool? HdrEnabled = null);

/// <summary>Capture-time omissions are diagnostics and are not persisted as profile data.</summary>
public sealed record ProfileCaptureResult(
    MonitorProfile Profile,
    IReadOnlyList<OperationIssue> OmittedValues);
