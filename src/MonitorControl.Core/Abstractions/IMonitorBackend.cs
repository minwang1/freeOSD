using MonitorControl.Core.Models;

namespace MonitorControl.Core.Abstractions;

/// <summary>
/// Platform implementation of monitor discovery and DDC/CI VCP access. Unsupported,
/// unavailable, disconnected, access-denied, and read/write failures are reported as
/// <see cref="BackendOperationException"/> with the corresponding typed kind. A VCP
/// feature found by probing rather than declared by the monitor must be marked
/// <see cref="VcpCapabilityDiscovery.ProbeOnly"/> in its capability record.
/// An empty discovery result means no monitors are currently accessible; a failure to
/// enumerate is an exception. Cancellation is honored before a hardware operation
/// begins. Once a write command has been issued, cancellation must not cause the
/// operation to be reported as Cancelled while a possible side effect remains silently
/// uncertain. A plain write does not require readback; readback is required only when
/// the calling use case promises verification. If cancellation arrives after a write
/// but before its outcome is known, the implementation/use case must reconcile the
/// outcome or report an error/partial result instead of silently claiming cancellation.
/// Restoration operations in higher-level use cases must not pass a cancellation token
/// once restoration begins.
/// </summary>
public interface IMonitorBackend
{
    ValueTask<IReadOnlyList<MonitorDescriptor>> DiscoverMonitorsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<MonitorCapabilities> GetCapabilitiesAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default);

    ValueTask<VcpValue> ReadVcpAsync(
        MonitorId monitorId,
        byte code,
        CancellationToken cancellationToken = default);

    ValueTask WriteVcpAsync(
        MonitorId monitorId,
        byte code,
        uint value,
        CancellationToken cancellationToken = default);
}
