using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MonitorControl.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsTaskbarAttention
{
    private const uint FlashwAll = 0x00000003;
    private const uint FlashwTimerNoForeground = 0x0000000C;

    public static void Flash(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero) return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = windowHandle,
            Flags = FlashwAll | FlashwTimerNoForeground,
            Count = 4,
            Timeout = 0
        };
        Native.FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlashWindowEx(ref FlashWindowInfo info);
    }
}
