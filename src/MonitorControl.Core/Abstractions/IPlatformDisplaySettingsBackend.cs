using MonitorControl.Core.Models;

namespace MonitorControl.Core.Abstractions;

/// <summary>
/// Optional OS display controls; implementations report support per operation. Trial
/// deadlines are owned by a Core/use-case coordinator, not by a view or platform API.
/// The coordinator must remain alive when the UI closes and must invoke restoration
/// when the deadline expires or the user cancels; both decisions use the single restore
/// operation below. Process termination recovery requires a separate OS-level watchdog
/// and is not guaranteed by this in-process contract.
/// </summary>
public interface IPlatformDisplaySettingsBackend
{
    ValueTask<PlatformDisplayCapabilities> GetCapabilitiesAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DisplayMode>> GetAvailableModesAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the requested mode and returns a transaction containing exact snapshots
    /// for both prior and requested modes. The caller allocates the transaction key and
    /// the returned trial must contain that same id; the backend must use it for all
    /// confirm/restore calls. Cancellation is honored only before changing the mode.
    /// Once the side effect starts, the backend must return the trial state or throw a
    /// typed error describing the uncertain outcome.
    /// </summary>
    ValueTask<DisplayModeTrial> ApplyDisplayModeTrialAsync(
        string trialId,
        MonitorId monitorId,
        DisplayMode requestedMode,
        DisplayModeChangeKind changeKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the trial and discards its rollback obligation. Confirm and restore are
    /// competing terminal decisions: exactly one may win. Repeating the winning decision
    /// is idempotent; requesting the opposite decision after a terminal transition fails
    /// with <see cref="BackendErrorKind.InvalidState"/>. Cancellation is honored only
    /// before the commit transition wins.
    /// </summary>
    ValueTask ConfirmDisplayModeTrialAsync(string trialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the exact previous mode for the trial. The coordinator calls this both
    /// when the user cancels and when the confirmation deadline expires. Confirm and
    /// restore are competing terminal decisions: exactly one may win. Repeating the
    /// winning restore decision is idempotent; requesting restore after confirm fails
    /// with InvalidState. Deliberately has no cancellation token: once restoration begins
    /// it must run to completion and report success/failure; failures use RestoreFailure
    /// and retain enough state to permit retry.
    /// </summary>
    ValueTask RestoreDisplayModeTrialAsync(string trialId);

    ValueTask<HdrState> GetHdrStateAsync(
        MonitorId monitorId,
        CancellationToken cancellationToken = default);

    ValueTask SetHdrStateAsync(
        MonitorId monitorId,
        bool enabled,
        CancellationToken cancellationToken = default);
}
