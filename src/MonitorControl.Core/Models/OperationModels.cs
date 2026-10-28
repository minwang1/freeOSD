namespace MonitorControl.Core.Models;

public enum OperationStage
{
    Queued,
    Discovering,
    Reading,
    Writing,
    WaitingForDisplay,
    Verifying,
    Restoring,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Progress suitable for UI display. One operation id has one Queued event, zero or
/// more active-stage events, and exactly one terminal event. Percent, when present,
/// should be monotonic within the operation. Events are delivered on the producer's
/// thread; observers must marshal to their own UI dispatcher.
/// </summary>
public sealed record OperationProgress(
    string OperationId,
    OperationStage Stage,
    string Message,
    int? Percent = null,
    MonitorId? MonitorId = null);

public sealed class OperationProgressEventArgs(OperationProgress progress) : EventArgs
{
    public OperationProgress Progress { get; } = progress;
}

public enum OperationOutcome
{
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled
}

public sealed record OperationIssue(string Scope, string Message, string? Code = null);

/// <summary>
/// Stable error category for backend failures. Unsupported is a permanent capability
/// limitation; Unavailable is a temporary platform/service condition; Disconnected
/// means a previously discovered monitor is no longer present. ReadFailure and
/// WriteFailure identify the failed DDC/CI operation, while AccessDenied identifies a
/// permission/security rejection. RestoreFailure must be surfaced with recovery guidance.
/// </summary>
public enum BackendErrorKind
{
    Unsupported,
    Unavailable,
    Disconnected,
    AccessDenied,
    ReadFailure,
    WriteFailure,
    InvalidValue,
    InvalidState,
    RestoreFailure,
    Unknown
}

/// <summary>Typed failure returned by a platform adapter through task exceptions.</summary>
public sealed class BackendOperationException : Exception
{
    public BackendOperationException(
        BackendErrorKind kind,
        string message,
        MonitorId? monitorId = null,
        byte? vcpCode = null,
        string? platformCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        MonitorId = monitorId;
        VcpCode = vcpCode;
        PlatformCode = platformCode;
        IsTransient = isTransient;
    }

    public BackendErrorKind Kind { get; }
    public MonitorId? MonitorId { get; }
    public byte? VcpCode { get; }
    public string? PlatformCode { get; }
    public bool IsTransient { get; }
}

public sealed record OperationResult(
    OperationOutcome Outcome,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool IsSuccess => Outcome is OperationOutcome.Succeeded or OperationOutcome.PartiallySucceeded;

    public static OperationResult Success() => new(OperationOutcome.Succeeded, Array.Empty<OperationIssue>());
}
