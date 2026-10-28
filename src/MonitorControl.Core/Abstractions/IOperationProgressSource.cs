using MonitorControl.Core.Models;

namespace MonitorControl.Core.Abstractions;

/// <summary>
/// Framework-neutral progress stream for long-running operations. Each operation emits
/// exactly one terminal stage (Completed, Failed, or Cancelled). Progress notifications
/// are raised on the producer's thread and never require a UI dispatcher; subscribers
/// must marshal updates themselves. Cancellation is best-effort before a side effect.
/// Once restoration starts it must not be canceled, even if the initiating UI closes.
/// </summary>
public interface IOperationProgressSource
{
    event EventHandler<OperationProgressEventArgs>? ProgressChanged;
}
