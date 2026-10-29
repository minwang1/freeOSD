using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MonitorControl.App.Diagnostics;
using MonitorControl.App.ViewModels;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Core.Services;
using MonitorControl.Platform.Windows;

namespace MonitorControl.App;

public partial class App : Application
{
    private RefreshRateTrialCoordinator? _refreshRateTrialCoordinator;
    private bool _allowShutdown;
    private int _shutdownResolutionRunning;

    public override void Initialize()
    {
        DiagnosticLog.Initialize();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
            {
                var monitorBackend = new WindowsMonitorBackend();
                var displaySettingsBackend = new WindowsDisplaySettingsBackend();
                _refreshRateTrialCoordinator = new RefreshRateTrialCoordinator(displaySettingsBackend);
                desktop.ShutdownRequested += OnShutdownRequested;
                desktop.MainWindow = new MainWindow(new ControlViewModel(
                    monitorBackend,
                    displaySettingsBackend,
                    _refreshRateTrialCoordinator));
            }
            else
            {
                desktop.MainWindow = new MainWindow(new ControlViewModel(new UnsupportedPlatformMonitorBackend()));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs eventArgs)
    {
        if (_allowShutdown || _refreshRateTrialCoordinator is null)
        {
            return;
        }

        var activeTrials = _refreshRateTrialCoordinator.GetStates()
            .Where(state => state.Status is not RefreshRateTrialStatus.Confirmed and not RefreshRateTrialStatus.Restored);
        if (!activeTrials.Any())
        {
            return;
        }

        // Keep the window alive until any unconfirmed display mode has been restored.
        eventArgs.Cancel = true;
        if (Interlocked.Exchange(ref _shutdownResolutionRunning, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var trial in activeTrials)
            {
                await _refreshRateTrialCoordinator.CancelAsync(trial.Trial.TrialId);
            }

            _allowShutdown = true;
            if (sender is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch
        {
            // A failed restore is kept visible by the ViewModel and may be retried.
            // A subsequent close request retries it instead of discarding the trial.
        }
        finally
        {
            Interlocked.Exchange(ref _shutdownResolutionRunning, 0);
        }
    }

    private sealed class UnsupportedPlatformMonitorBackend : IMonitorBackend
    {
        private static BackendOperationException Unsupported() => new(
            BackendErrorKind.Unsupported,
            "当前平台的 DDC/CI 后端尚未实现。Windows 版本目前提供显示器控制。");

        public ValueTask<IReadOnlyList<MonitorDescriptor>> DiscoverMonitorsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<MonitorDescriptor>>(Unsupported());

        public ValueTask<MonitorCapabilities> GetCapabilitiesAsync(MonitorId monitorId, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MonitorCapabilities>(Unsupported());

        public ValueTask<VcpValue> ReadVcpAsync(MonitorId monitorId, byte code, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<VcpValue>(Unsupported());

        public ValueTask WriteVcpAsync(MonitorId monitorId, byte code, uint value, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Unsupported());
    }
}
