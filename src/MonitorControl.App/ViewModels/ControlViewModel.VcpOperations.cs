using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;
using MonitorControl.Core.Services;
using MonitorControl.App.Diagnostics;
using MonitorControl.App.Services;

namespace MonitorControl.App.ViewModels;

public sealed partial class ControlViewModel : ObservableObject
{
    private async Task WriteAndVerifyAsync(MonitorDescriptor monitor, VcpControlViewModel control, uint value)
    {
        StatusText = $"正在写入 {control.Name}…";
        DiagnosticLog.Write("INFO", "control.vcp.apply.started", $"code=0x{control.Code:X2} target={value}");
        control.IsWriting = true;
        await _deviceGate.WaitAsync();
        try
        {
            if (SelectedMonitor?.Id != monitor.Id) return;

            Exception? writeError = null;
            try
            {
                await _backend.WriteVcpAsync(monitor.Id, control.Code, value);
            }
            catch (Exception exception)
            {
                // Some monitor drivers can report a failed command even when the
                // monitor applied it. Read back before deciding what the UI should say.
                writeError = exception;
            }

            try
            {
                var readback = await ReadVcpForVerificationAsync(monitor.Id, control.Code, value);
                DiagnosticLog.Write("INFO", "control.vcp.apply.readback", $"code=0x{control.Code:X2} target={value} actual={readback.Current} matches={readback.Current == value}");
                if (SelectedMonitor?.Id == monitor.Id)
                {
                    if (!control.IsSelectionCurrent(value))
                    {
                        StatusText = $"{control.Name} 的先前写入已完成；当前新选择尚未应用。";
                    }
                    else
                    {
                        control.SetReadValue(readback.Current);
                        if (readback.Current == value)
                        {
                            StatusText = writeError is null
                                ? $"{control.Name} 已写入并读回确认。"
                                : $"{control.Name} 的写入调用报告错误，但显示器读回目标值。";
                            control.ErrorText = writeError?.Message;
                        }
                        else
                        {
                            StatusText = $"{control.Name} 写入后读回为 {readback.Current}，目标值为 {value}。";
                            control.ErrorText = writeError is null
                                ? $"显示器返回 {readback.Current}（目标 {value}）。"
                                : $"写入错误：{writeError.Message}；显示器返回 {readback.Current}（目标 {value}）。";
                        }
                    }
                }
            }
            catch (Exception readError)
            {
                DiagnosticLog.Write("WARN", "control.vcp.apply.readback_failed", $"code=0x{control.Code:X2} target={value} errorType={readError.GetType().Name} message={readError.Message}");
                if (SelectedMonitor?.Id == monitor.Id && control.IsSelectionCurrent(value))
                {
                    control.MarkValueUnknown();
                    var detail = writeError is null
                        ? readError.Message
                        : $"写入错误：{writeError.Message}；读回错误：{readError.Message}";
                    control.ErrorText = detail;
                    StatusText = $"{control.Name} 的写入状态未确认（已重试读回）；请重新扫描确认实际值：{detail}";
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("ERROR", "control.vcp.apply.failed", $"code=0x{control.Code:X2} target={value} errorType={exception.GetType().Name} message={exception.Message}");
            if (SelectedMonitor?.Id == monitor.Id && control.IsSelectionCurrent(value))
            {
                control.RestoreConfirmedValue();
                control.ErrorText = exception.Message;
                StatusText = $"写入 {control.Name} 失败：{exception.Message}";
            }
        }
        finally
        {
            _deviceGate.Release();
            control.IsWriting = false;
        }
    }

    private async Task<VcpValue> ReadVcpForVerificationAsync(MonitorId monitorId, byte code, uint target)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < VcpReadbackRetryDelays.Length; attempt++)
        {
            await Task.Delay(VcpReadbackRetryDelays[attempt]);
            try
            {
                var value = await _backend.ReadVcpAsync(monitorId, code);
                if (attempt > 0)
                {
                    DiagnosticLog.Write("INFO", "control.vcp.readback.retry_succeeded", $"code=0x{code:X2} target={target} actual={value.Current} attempt={attempt + 1}");
                }
                return value;
            }
            catch (BackendOperationException exception) when (exception.IsTransient)
            {
                lastError = exception;
                DiagnosticLog.Write("WARN", "control.vcp.readback.retry", $"code=0x{code:X2} target={target} attempt={attempt + 1} platformCode={exception.PlatformCode}");
            }
        }

        throw lastError ?? new BackendOperationException(
            BackendErrorKind.ReadFailure,
            $"Could not verify VCP 0x{code:X2} after transient read failures.",
            monitorId,
            code);
    }

    private void ClearControls()
    {
        ContinuousControls.Clear();
        DiscreteControls.Clear();
        NotifyControlSummaryChanged();
    }

    private void NotifyControlSummaryChanged()
    {
        OnPropertyChanged(nameof(HasContinuousControls));
        OnPropertyChanged(nameof(ControlPanelContinuousControls));
        OnPropertyChanged(nameof(HasNoContinuousControls));
        OnPropertyChanged(nameof(HasInputSource));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(HasNoMonitors));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(HasNoContinuousControls));
        if (RefreshDisplayColorProfilesCommand is AsyncCommand refresh) refresh.RaiseCanExecuteChanged();
        if (ImportDisplayColorProfileCommand is AsyncCommand import) import.RaiseCanExecuteChanged();
        if (DeleteGeneratedRgbProfileCommand is AsyncCommand delete) delete.RaiseCanExecuteChanged();
        if (ApplyDisplayColorProfileCommand is AsyncCommand apply) apply.RaiseCanExecuteChanged();
        if (RestoreDisplayColorProfileCommand is AsyncCommand restore) restore.RaiseCanExecuteChanged();
    }

    private static (string Name, uint Minimum, VcpValueOption[] Options) Describe(byte code) => code switch
    {
        0x10 => ("亮度", 0, []),
        0x12 => ("对比度", 0, []),
        0x16 => ("红色增益", 0, []),
        0x18 => ("绿色增益", 0, []),
        0x1A => ("蓝色增益", 0, []),
        0x14 => ("色彩预设", 0,
        [
            new(0x01, "sRGB"), new(0x04, "5000 K"), new(0x05, "6500 K"),
            new(0x06, "7500 K"), new(0x08, "9300 K"), new(0x0B, "用户 1"), new(0x0C, "用户 2")
        ]),
        0x60 => ("输入源", 0,
        [
            new(0x0F, "DisplayPort 1"), new(0x10, "DisplayPort 2 / DP"),
            new(0x11, "HDMI 1"), new(0x12, "DVI"), new(0x1B, "HDMI 2")
        ]),
        _ => ($"VCP 0x{code:X2}", 0, [])
    };

    private void SetContinuousValue(VcpControlViewModel control, double value) =>
        _ = WriteContinuousAfterDelayAsync(control, value);

    private static readonly TimeSpan SliderWriteDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan[] VcpReadbackRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    ];

    private async Task WriteContinuousAfterDelayAsync(VcpControlViewModel control, double value)
    {
        var token = control.RestartDebounce();
        try
        {
            await Task.Delay(SliderWriteDelay, token);
            if (token.IsCancellationRequested || control.Value != value) return;
            await ApplyContinuousAsync(control, (uint)Math.Round(value, MidpointRounding.AwayFromZero));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer slider value superseded this pending write.
        }
    }

    internal void OnContinuousValueChanged(VcpControlViewModel control, double value) => SetContinuousValue(control, value);
}
