using System.Runtime.Versioning;
using Microsoft.Win32;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;

namespace MonitorControl.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsInstalledApplicationCatalog : IInstalledApplicationCatalog
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<InstalledApplication>>(() => ReadApplications(cancellationToken), cancellationToken);

    private static IReadOnlyList<InstalledApplication> ReadApplications(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var appKey = uninstall.OpenSubKey(subKeyName);
                    if (appKey is null || appKey.GetValue("SystemComponent") is int systemComponent && systemComponent != 0) continue;
                    var name = appKey.GetValue("DisplayName") as string;
                    var icon = appKey.GetValue("DisplayIcon") as string;
                    var executable = ExtractExecutablePath(icon);
                    if (string.IsNullOrWhiteSpace(name) || executable is null || !File.Exists(executable)) continue;
                    results.TryAdd(executable, new InstalledApplication(name.Trim(), executable));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        IReadOnlyList<InstalledApplication> applications = results.Values
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return applications;
    }

    private static string? ExtractExecutablePath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        string candidate;
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            if (endQuote <= 1) return null;
            candidate = value[1..endQuote];
        }
        else
        {
            var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0) return null;
            candidate = value[..(exeEnd + 4)].Trim();
        }
        return Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(candidate)
            : null;
    }
}
