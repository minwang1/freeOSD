using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MonitorControl.Core.Abstractions;

namespace MonitorControl.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundApplicationMonitor : IForegroundApplicationMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly object _sync = new();
    private readonly Native.WinEventProc _callback;
    private IntPtr _hook;
    private string? _lastPath;
    private string? _previousPath;
    private bool _disposed;

    public WindowsForegroundApplicationMonitor() => _callback = OnForegroundChanged;

    public event EventHandler<ForegroundApplicationChangedEventArgs>? ForegroundApplicationChanged;

    public ValueTask<string?> GetPreviousForegroundExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return ValueTask.FromResult(_previousPath);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hook != IntPtr.Zero) return ValueTask.CompletedTask;
            _hook = Native.SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _callback, 0, 0, WineventOutOfContext);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException($"SetWinEventHook failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
        var foregroundWindow = Native.GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero)
            OnForegroundChanged(_hook, EventSystemForeground, foregroundWindow, 0, 0, 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            _lastPath = null;
            _previousPath = null;
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == IntPtr.Zero) return;
        var path = GetExecutablePath(window);
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_sync)
        {
            if (_disposed || _hook == IntPtr.Zero || string.Equals(_lastPath, path, StringComparison.OrdinalIgnoreCase)) return;
            _previousPath = _lastPath;
            _lastPath = path;
        }
        ForegroundApplicationChanged?.Invoke(this, new ForegroundApplicationChangedEventArgs(path));
    }

    private static string? GetExecutablePath(IntPtr window)
    {
        var threadId = Native.GetWindowThreadProcessId(window, out var processId);
        if (threadId == 0 || processId == 0) return null;
        var process = Native.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        string? path = null;
        try
        {
            var buffer = new StringBuilder(32768);
            var length = (uint)buffer.Capacity;
            if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString();
        }
        finally { Native.CloseHandle(process); }
        return path;
    }

    private static class Native
    {
        internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHook, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executableName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
