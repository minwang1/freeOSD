namespace MonitorControl.Core.Abstractions;

/// <summary>Controls whether the current user starts the application at sign-in.</summary>
public interface ILoginStartupService
{
    bool IsSupported { get; }
    ValueTask<bool> GetEnabledAsync(CancellationToken cancellationToken = default);
    ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
