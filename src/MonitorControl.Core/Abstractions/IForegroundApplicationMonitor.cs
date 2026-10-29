namespace MonitorControl.Core.Abstractions;

public sealed class ForegroundApplicationChangedEventArgs(string executablePath) : EventArgs
{
    public string ExecutablePath { get; } = executablePath;
}

/// <summary>Reports foreground executable changes without exposing native window handles.</summary>
public interface IForegroundApplicationMonitor : IDisposable
{
    event EventHandler<ForegroundApplicationChangedEventArgs>? ForegroundApplicationChanged;
    ValueTask<string?> GetPreviousForegroundExecutablePathAsync(CancellationToken cancellationToken = default);
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
