using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using MonitorControl.App.Diagnostics;
using MonitorControl.App.Services;
using MonitorControl.App.ViewModels;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Core.Services;
using MonitorControl.Platform.Windows;
using TKCode.Arc;

namespace MonitorControl.App;

public partial class App : Application
{
    private static App? _current;
    private RefreshRateTrialCoordinator? _refreshRateTrialCoordinator;
    private bool _allowShutdown;
    private int _shutdownResolutionRunning;
    private MainWindow? _mainWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _trayIcon;
    private AppearanceSettingsStore _appearanceSettingsStore = null!;
    private AppearanceMode _appearanceMode;
    private AccentColor _accentColor;

    public override void Initialize()
    {
        _current = this;
        DiagnosticLog.Initialize();
        AvaloniaXamlLoader.Load(this);
        _appearanceSettingsStore = new AppearanceSettingsStore();
        var appearanceSettings = _appearanceSettingsStore.Load();
        _appearanceMode = appearanceSettings.Mode;
        _accentColor = appearanceSettings.Accent;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            if (OperatingSystem.IsWindows())
            {
                var monitorBackend = new WindowsMonitorBackend();
                var displaySettingsBackend = new WindowsDisplaySettingsBackend();
                _refreshRateTrialCoordinator = new RefreshRateTrialCoordinator(displaySettingsBackend);
                desktop.ShutdownRequested += OnShutdownRequested;
                desktop.MainWindow = _mainWindow = new MainWindow(new ControlViewModel(
                    monitorBackend,
                    displaySettingsBackend,
                    _refreshRateTrialCoordinator,
                    loginStartupService: new WindowsLoginStartupService(),
                    foregroundApplicationMonitor: new WindowsForegroundApplicationMonitor(),
                    installedApplicationCatalog: new WindowsInstalledApplicationCatalog(),
                    displayColorProfileService: new WindowsDisplayColorProfileService()), _appearanceMode, _accentColor);
            }
            else
            {
                desktop.MainWindow = _mainWindow = new MainWindow(new ControlViewModel(new UnsupportedPlatformMonitorBackend()), _appearanceMode, _accentColor);
            }

            InitializeTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();

        try
        {
            ApplyArcTheme(_appearanceMode, _accentColor);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("ERROR", "appearance.theme.apply.failed", $"mode={_appearanceMode}; error={exception}");
            _appearanceMode = AppearanceMode.System;
        }
    }

    public static void SetAppearanceMode(AppearanceMode mode)
    {
        var app = _current;
        if (app is null) return;
        app.SetAppearance(mode, app._accentColor);
    }

    public static void SetAccentColor(AccentColor accentColor)
    {
        var app = _current;
        if (app is null) return;
        app.SetAppearance(app._appearanceMode, accentColor);
    }

    private void SetAppearance(AppearanceMode mode, AccentColor accentColor)
    {
        if (_appearanceMode == mode && _accentColor == accentColor) return;
        try
        {
            ApplyArcTheme(mode, accentColor);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("ERROR", "appearance.theme.apply.failed", $"mode={mode}; accent={accentColor}; error={exception}");
            return;
        }

        _appearanceMode = mode;
        _accentColor = accentColor;
        try { _appearanceSettingsStore.Save(new AppearanceSettings(mode, accentColor)); }
        catch (Exception exception)
        {
            DiagnosticLog.Write("WARN", "appearance.settings.save.failed", $"error={exception.Message}");
        }
    }

    private static void ApplyArcTheme(AppearanceMode mode, AccentColor accentColor) => ArcTheme.Apply(theme => theme
        .Appearance(mode switch
        {
            AppearanceMode.Light => ArcAppearance.Light,
            AppearanceMode.Dark => ArcAppearance.Dark,
            _ => ArcAppearance.System
        })
        .Accent(accentColor switch
        {
            AccentColor.SoftGreen => Color.FromRgb(105, 157, 130),
            AccentColor.SoftOrange => Color.FromRgb(204, 139, 94),
            AccentColor.Charcoal => Color.FromRgb(80, 88, 96),
            _ => Color.FromRgb(105, 151, 190)
        }));

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new NativeMenu();
            var openItem = new NativeMenuItem("打开 Monitor Control");
            openItem.Click += (_, _) => ShowMainWindow();
            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(openItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MonitorControl.App/Assets/monitor-control.ico"))),
                ToolTipText = "Monitor Control",
                Menu = menu
            };
            _trayIcon.Clicked += (_, _) => ShowMainWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            if (_mainWindow is not null) _mainWindow.TrayAvailable = true;
        }
        catch (Exception exception)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            DiagnosticLog.Write("WARN", "tray.initialize.failed", $"error={exception.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public static void ActivatePrimaryWindow()
    {
        if (_current?._mainWindow is not { } window) return;
        _current.ShowMainWindow();
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { } handle)
            WindowsTaskbarAttention.Flash(handle.Handle);
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null) _mainWindow.AllowClose = true;
        _desktop?.Shutdown();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs eventArgs)
    {
        if (_allowShutdown)
        {
            return;
        }

        var activeTrials = _refreshRateTrialCoordinator?.GetStates()
            .Where(state => state.Status is not RefreshRateTrialStatus.Confirmed and not RefreshRateTrialStatus.Restored);
        var hasActiveTrials = activeTrials?.Any() == true;
        var viewModel = _mainWindow?.DataContext as ControlViewModel;
        var hasPendingPreview = viewModel?.HasPendingRgbPreview == true;
        if (!hasActiveTrials && !hasPendingPreview)
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
            if (hasPendingPreview && viewModel is not null && !await viewModel.RestoreRgbPreviewBeforeExitAsync())
            {
                return;
            }

            if (_refreshRateTrialCoordinator is not null)
            {
                foreach (var trial in activeTrials ?? [])
                {
                    await _refreshRateTrialCoordinator.CancelAsync(trial.Trial.TrialId);
                }
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
