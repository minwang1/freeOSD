using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MonitorControl.App.ViewModels;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Platform.Windows;

namespace MonitorControl.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IMonitorBackend backend = OperatingSystem.IsWindows()
                ? new WindowsMonitorBackend()
                : new UnsupportedPlatformMonitorBackend();
            desktop.MainWindow = new MainWindow(new ControlViewModel(backend));
        }

        base.OnFrameworkInitializationCompleted();
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
