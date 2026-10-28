namespace MonitorControl.Core.Models;

/// <summary>A stable identifier for a monitor during the current discovery session.</summary>
public readonly record struct MonitorId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Display identity and connection metadata. It contains no OS handles.</summary>
public sealed record MonitorDescriptor(
    MonitorId Id,
    string Name,
    string? Manufacturer = null,
    string? Model = null,
    string? SerialNumber = null,
    string? ConnectionDescription = null);

public enum VcpFeatureKind
{
    Continuous,
    NonContinuous,
    ReadOnly,
    Unknown
}

/// <summary>Whether the monitor declared a VCP code or it was found by an explicit probe.</summary>
public enum VcpCapabilityDiscovery
{
    Advertised,
    ProbeOnly
}

/// <summary>
/// One VCP feature advertised or discovered on a monitor. Availability distinguishes
/// known unsupported from temporarily unavailable or not yet determined; Discovery
/// independently distinguishes advertised from probed.
/// </summary>
public sealed record VcpFeature(
    byte Code,
    string? Name,
    VcpFeatureKind Kind,
    uint? MaximumValue = null,
    IReadOnlyDictionary<uint, string>? EnumeratedValues = null,
    VcpCapabilityDiscovery Discovery = VcpCapabilityDiscovery.Advertised,
    CapabilityAvailability Availability = CapabilityAvailability.Unknown);

/// <summary>Current and maximum values returned by a VCP read.</summary>
public readonly record struct VcpValue(byte Code, uint Current, uint? Maximum = null);

/// <summary>Capabilities discovered for a specific monitor.</summary>
public sealed record MonitorCapabilities(
    IReadOnlyList<VcpFeature> VcpFeatures,
    string? RawCapabilities = null,
    string? VcpVersion = null,
    IReadOnlyList<string>? Limitations = null);
