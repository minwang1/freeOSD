using Avalonia;
using System;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;
using TKCode.Arc;
using MonitorControl.App.Diagnostics;
using MonitorControl.Platform.Windows;

namespace MonitorControl.App;

class Program
{
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activatePrimaryEvent;
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            _instanceMutex = new Mutex(initiallyOwned: true, name: "Local\\MonitorControl.SingleInstance", out var createdNew);
            _activatePrimaryEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MonitorControl.ActivatePrimary");
            if (!createdNew)
            {
                _activatePrimaryEvent.Set();
                _activatePrimaryEvent.Dispose();
                _activatePrimaryEvent = null;
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return;
            }

            _ = Task.Run(WaitForActivationRequests);
        }

        DiagnosticLog.Initialize();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        _activatePrimaryEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }

    private static void WaitForActivationRequests()
    {
        while (_activatePrimaryEvent is { } activationEvent)
        {
            try
            {
                if (!activationEvent.WaitOne()) return;
                Dispatcher.UIThread.Post(App.ActivatePrimaryWindow);
            }
            catch (ObjectDisposedException) { return; }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseArc(arc => arc
                .UseArcControls()
                .WithTheme(theme => theme.Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Neutral)))
            .LogToTrace();
}
