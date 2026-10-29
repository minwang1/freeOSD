using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;

namespace MonitorControl.Platform.Windows;

/// <summary>Windows DXVA2 implementation of monitor discovery and DDC/CI VCP access.</summary>
public sealed class WindowsMonitorBackend : IMonitorBackend
{
    private const int InvalidDdcciCommandError = unchecked((int)0xC0262589);
    private readonly object _sync = new();
    private Dictionary<MonitorId, MonitorRoute> _routes = new();
    private readonly ConcurrentDictionary<MonitorId, SemaphoreSlim> _monitorGates = new();

    public async ValueTask<IReadOnlyList<MonitorDescriptor>> DiscoverMonitorsAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run<IReadOnlyList<MonitorDescriptor>>(() =>
        {
            var discovered = EnumeratePhysicalMonitors(cancellationToken);
            try
            {
                var routes = new Dictionary<MonitorId, MonitorRoute>();
                var descriptors = new List<MonitorDescriptor>();
                foreach (var item in discovered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = new MonitorId($"{item.DisplayName}|{item.Index}|{item.Description}");
                    routes[id] = new MonitorRoute(item.DisplayName, item.Index, item.Description);
                    descriptors.Add(new MonitorDescriptor(id, item.Description, ConnectionDescription: item.DisplayName));
                }
                lock (_sync) _routes = routes;
                return descriptors;
            }
            finally
            {
                foreach (var item in discovered) item.Handle.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MonitorCapabilities> GetCapabilitiesAsync(MonitorId monitorId, CancellationToken cancellationToken = default) =>
        WithMonitorAsync(monitorId, cancellationToken, (handle, token) =>
        {
            var raw = ReadCapabilities(handle);
            var features = ParseCapabilities(raw);
            Trace.TraceInformation($"ddcci.capabilities.completed featureCount={features.Count} responseReceived={!string.IsNullOrWhiteSpace(raw)}");
            return ValueTask.FromResult(new MonitorCapabilities(features, raw, Limitations: string.IsNullOrWhiteSpace(raw) ? ["The monitor did not return a DDC/CI capability string."] : null));
        });

    public ValueTask<VcpValue> ReadVcpAsync(MonitorId monitorId, byte code, CancellationToken cancellationToken = default) =>
        WithMonitorAsync(monitorId, cancellationToken, (handle, cancellation) =>
        {
            Trace.TraceInformation($"ddcci.vcp.read.started code=0x{code:X2}");
            if (!Native.GetVCPFeatureAndVCPFeatureReply(handle, code, out var type, out var current, out var maximum))
            {
                var errorCode = Marshal.GetLastPInvokeError();
                Trace.TraceWarning($"ddcci.vcp.read.failed code=0x{code:X2} win32Error={errorCode}");
                throw NativeError(BackendErrorKind.ReadFailure, $"Could not read VCP 0x{code:X2}.", monitorId, code, errorCode);
            }
            Trace.TraceInformation($"ddcci.vcp.read.completed code=0x{code:X2} vcpType={type} current={current} maximum={maximum}");
            return ValueTask.FromResult(new VcpValue(code, current, maximum));
        });

    public ValueTask WriteVcpAsync(MonitorId monitorId, byte code, uint value, CancellationToken cancellationToken = default) =>
        WithMonitorAsync(monitorId, cancellationToken, (handle, _) =>
        {
            Trace.TraceInformation($"ddcci.vcp.write.started code=0x{code:X2} target={value}");
            if (!Native.SetVCPFeature(handle, code, value))
            {
                var errorCode = Marshal.GetLastPInvokeError();
                Trace.TraceWarning($"ddcci.vcp.write.failed code=0x{code:X2} target={value} win32Error={errorCode}");
                throw NativeError(BackendErrorKind.WriteFailure, $"Could not write VCP 0x{code:X2}.", monitorId, code, errorCode);
            }
            Trace.TraceInformation($"ddcci.vcp.write.completed code=0x{code:X2} target={value}");
            return ValueTask.CompletedTask;
        });

    private async ValueTask<T> WithMonitorAsync<T>(MonitorId id, CancellationToken token, Func<IntPtr, CancellationToken, ValueTask<T>> action)
    {
        EnsureWindows(); token.ThrowIfCancellationRequested();
        MonitorRoute route;
        lock (_sync)
            if (!_routes.TryGetValue(id, out route!))
                throw new BackendOperationException(BackendErrorKind.Disconnected, "Monitor ID is stale; rescan monitors.", id);

        var gate = _monitorGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var safeHandle = OpenMonitor(route, token);
                token.ThrowIfCancellationRequested();
                return await action(safeHandle.DangerousGetHandle(), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask WithMonitorAsync(MonitorId id, CancellationToken token, Func<IntPtr, CancellationToken, ValueTask> action)
    {
        EnsureWindows(); token.ThrowIfCancellationRequested();
        MonitorRoute route;
        lock (_sync)
            if (!_routes.TryGetValue(id, out route!))
                throw new BackendOperationException(BackendErrorKind.Disconnected, "Monitor ID is stale; rescan monitors.", id);

        var gate = _monitorGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var safeHandle = OpenMonitor(route, token);
                token.ThrowIfCancellationRequested();
                await action(safeHandle.DangerousGetHandle(), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SafePhysicalMonitorHandle OpenMonitor(MonitorRoute route, CancellationToken token)
    {
        var discovered = EnumeratePhysicalMonitors(token);
        SafePhysicalMonitorHandle? selected = null;
        try
        {
            foreach (var item in discovered)
            {
                if (selected is null && item.DisplayName == route.DisplayName &&
                    item.Index == route.Index && item.Description == route.Description)
                {
                    selected = item.Handle;
                }
                else
                {
                    item.Handle.Dispose();
                }
            }
        }
        catch
        {
            foreach (var item in discovered)
                if (!ReferenceEquals(item.Handle, selected)) item.Handle.Dispose();
            selected?.Dispose();
            throw;
        }

        if (selected is not null) return selected;
        throw new BackendOperationException(BackendErrorKind.Disconnected, "The physical monitor is no longer available.");
    }

    private static List<DiscoveredMonitor> EnumeratePhysicalMonitors(CancellationToken token)
    {
        var result = new List<DiscoveredMonitor>();
        Exception? callbackException = null;
        Native.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            if (token.IsCancellationRequested) { callbackException = new OperationCanceledException(token); return false; }
            var info = new Native.MonitorInfoEx { Size = (uint)Marshal.SizeOf<Native.MonitorInfoEx>() };
            if (!Native.GetMonitorInfo(hMonitor, ref info)) return true;
            if (!Native.GetNumberOfPhysicalMonitors(hMonitor, out var count) || count == 0) return true;
            var monitors = new Native.PhysicalMonitor[count];
            if (!Native.GetPhysicalMonitors(hMonitor, count, monitors)) return true;
            var wrapped = monitors.Select(pm => new SafePhysicalMonitorHandle(pm.Handle)).ToArray();
            try
            {
                for (var i = 0; i < monitors.Length; i++)
                    result.Add(new DiscoveredMonitor(info.DeviceName, i, monitors[i].Description, wrapped[i]));
            }
            catch (Exception exception)
            {
                foreach (var handle in wrapped) handle.Dispose();
                callbackException = exception;
                return false;
            }
            return true;
        };
        try
        {
            var enumerated = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            if (callbackException is not null) throw callbackException;
            if (!enumerated)
                throw NativeError(BackendErrorKind.Unavailable, "Could not enumerate display monitors.");
            return result;
        }
        catch
        {
            foreach (var item in result) item.Handle.Dispose();
            throw;
        }
    }

    private static string ReadCapabilities(IntPtr handle)
    {
        if (!Native.GetCapabilitiesStringLength(handle, out var length) || length == 0 || length > 65536) return string.Empty;
        var buffer = new StringBuilder((int)length + 1);
        return Native.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length) ? buffer.ToString() : string.Empty;
    }

    private static IReadOnlyList<VcpFeature> ParseCapabilities(string capabilities)
    {
        var start = Regex.Match(capabilities, @"vcp\s*\(", RegexOptions.IgnoreCase);
        if (!start.Success) return [];
        var open = capabilities.IndexOf('(', start.Index);
        var depth = 1; var end = open + 1;
        for (; end < capabilities.Length && depth > 0; end++)
        {
            if (capabilities[end] == '(') depth++;
            else if (capabilities[end] == ')') depth--;
        }
        if (depth != 0) return [];
        var body = capabilities[(open + 1)..(end - 1)];
        var features = new List<VcpFeature>();
        var pos = 0;
        while (pos < body.Length)
        {
            while (pos < body.Length && !Uri.IsHexDigit(body[pos])) pos++;
            var codeStart = pos;
            while (pos < body.Length && Uri.IsHexDigit(body[pos])) pos++;
            if (pos - codeStart != 2) { if (pos == codeStart) pos++; continue; }
            var code = Convert.ToByte(body.Substring(codeStart, 2), 16);
            var values = new Dictionary<uint, string>();
            if (pos < body.Length && body[pos] == '(')
            {
                var enumStart = ++pos; var enumDepth = 1;
                while (pos < body.Length && enumDepth > 0) { if (body[pos] == '(') enumDepth++; else if (body[pos] == ')') enumDepth--; pos++; }
                if (enumDepth == 0)
                {
                    foreach (Match match in Regex.Matches(body[enumStart..(pos - 1)], @"(?<![0-9A-F])([0-9A-F]{2})(?![0-9A-F])", RegexOptions.IgnoreCase))
                    { var value = Convert.ToUInt32(match.Groups[1].Value, 16); values[value] = $"0x{value:X2}"; }
                }
            }
            features.Add(new VcpFeature(code, FeatureNames.GetValueOrDefault(code), values.Count == 0 ? VcpFeatureKind.Continuous : VcpFeatureKind.NonContinuous,
                EnumeratedValues: values.Count == 0 ? null : values, Discovery: VcpCapabilityDiscovery.Advertised, Availability: CapabilityAvailability.Unknown));
        }
        return features;
    }

    private static readonly IReadOnlyDictionary<byte, string> FeatureNames = new Dictionary<byte, string>
    {
        [0x10] = "Brightness", [0x12] = "Contrast", [0x14] = "Color preset", [0x16] = "Red gain",
        [0x18] = "Green gain", [0x1A] = "Blue gain", [0x60] = "Input source", [0x62] = "Speaker volume",
        [0xAA] = "Screen orientation", [0xAC] = "Sharpness", [0xD6] = "Power mode", [0xDF] = "VCP version"
    };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new BackendOperationException(BackendErrorKind.Unsupported, "The DXVA2 monitor backend is available only on Windows.");
    }

    private static BackendOperationException NativeError(BackendErrorKind kind, string message, MonitorId? id = null, byte? code = null, int? nativeErrorCode = null)
    {
        var errorCode = nativeErrorCode ?? Marshal.GetLastPInvokeError();
        return new BackendOperationException(kind, message, id, code, errorCode.ToString(),
            isTransient: errorCode == InvalidDdcciCommandError,
            innerException: new Win32Exception(errorCode));
    }

    private sealed record MonitorRoute(string DisplayName, int Index, string Description);
    private sealed record DiscoveredMonitor(string DisplayName, int Index, string Description, SafePhysicalMonitorHandle Handle);

    private sealed class SafePhysicalMonitorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePhysicalMonitorHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => Native.DestroyPhysicalMonitor(handle);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfoEx
        {
            public uint Size; public RECT Monitor; public RECT Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }
        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNumberOfPhysicalMonitors(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPhysicalMonitors(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physicalMonitors);
        [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);
        [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
        [DllImport("dxva2.dll", EntryPoint = "CapabilitiesRequestAndCapabilitiesReply", CharSet = CharSet.Ansi, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, StringBuilder capabilities, uint length);
        [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    }
}
