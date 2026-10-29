using Microsoft.Win32;
using MonitorControl.Core.Abstractions;
using System.Runtime.Versioning;

namespace MonitorControl.Platform.Windows;

/// <summary>Per-user Windows sign-in startup through the current-user Run key.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLoginStartupService : ILoginStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorControl";

    public bool IsSupported => OperatingSystem.IsWindows();

    public ValueTask<bool> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var registeredPath = key?.GetValue(ValueName) as string;
        return ValueTask.FromResult(string.Equals(registeredPath, GetExecutableCommand(), StringComparison.OrdinalIgnoreCase));
    }

    public ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, GetExecutableCommand(), RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return ValueTask.CompletedTask;
    }

    private static string GetExecutableCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Could not determine the application executable path.");
        return $"\"{path}\"";
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Login startup is currently supported on Windows only.");
    }
}
