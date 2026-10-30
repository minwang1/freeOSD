using MonitorControl.Core.Models;

namespace MonitorControl.Core.Abstractions;

public interface IDisplayColorProfileService
{
    bool IsSupported { get; }
    Task<IReadOnlyList<DisplayColorProfile>> GetProfilesAsync(string displayName, CancellationToken cancellationToken = default);
    Task<string?> GetActiveProfilePathAsync(string displayName, CancellationToken cancellationToken = default);
    Task<DisplayColorPipelineCapabilities> GetPipelineCapabilitiesAsync(string displayName, CancellationToken cancellationToken = default);
    Task<string> ImportProfileAsync(string displayName, string sourcePath, CancellationToken cancellationToken = default);
    Task<string> ApplyRgbAdjustmentAsync(string displayName, RgbGainAdjustment gains, CancellationToken cancellationToken = default);
    Task ApplyProfileAsync(string displayName, string profilePath, CancellationToken cancellationToken = default);
    Task RestoreOriginalProfileAsync(string displayName, CancellationToken cancellationToken = default);
    Task<bool> HasOriginalProfileAsync(string displayName, CancellationToken cancellationToken = default);
}
