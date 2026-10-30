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
    private async Task ApplyRefreshRateAsync()
    {
        var mode = SelectedRefreshRateMode;
        if (mode is not null) await ApplyDisplayModeTrialAsync(mode, isResolution: false);
    }

    private async Task ToggleHdrAsync()
    {
        var monitor = SelectedMonitor;
        if (_displaySettingsBackend is null || monitor is null || !IsHdrSupported) return;
        var requested = !IsHdrEnabled;
        HdrStatus = requested ? "正在开启 HDR…" : "正在关闭 HDR…";
        DiagnosticLog.Write("INFO", "display.hdr.change.started", $"enabled={requested}");
        try
        {
            await _displaySettingsBackend.SetHdrStateAsync(monitor.Id, requested);
            var actual = await _displaySettingsBackend.GetHdrStateAsync(monitor.Id);
            IsHdrSupported = actual.IsSupported;
            IsHdrEnabled = actual.IsEnabled;
            HdrStatus = actual.IsEnabled == requested ? (requested ? "HDR 已开启。" : "HDR 已关闭。") : "Windows 返回成功，但 HDR 状态读回不匹配。";
            DiagnosticLog.Write(actual.IsEnabled == requested ? "INFO" : "WARN", "display.hdr.change.readback", $"requested={requested} actual={actual.IsEnabled} supported={actual.IsSupported}");
        }
        catch (Exception exception)
        {
            HdrStatus = $"HDR 设置失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.hdr.change.failed", $"requested={requested} errorType={exception.GetType().Name} message={exception.Message}");
        }
    }

    private void PopulateRefreshRatesForResolution(DisplayMode? resolution)
    {
        var previousSelection = SelectedRefreshRateMode;
        RefreshRateModes.Clear();
        var rates = resolution is null
            ? []
            : _availableDisplayModes.Where(mode => mode.Width == resolution.Width && mode.Height == resolution.Height && mode.BitsPerPixel == resolution.BitsPerPixel)
                .OrderByDescending(mode => mode.RefreshRateNumerator / (double)Math.Max(1, mode.RefreshRateDenominator)).ToArray();
        foreach (var rate in rates) RefreshRateModes.Add(rate);
        OnPropertyChanged(nameof(HasRefreshRateModes));
        OnPropertyChanged(nameof(HasNoRefreshRateModes));
        var currentRate = rates.FirstOrDefault(mode => mode.IsCurrent);
        SelectedRefreshRateMode = currentRate ?? FindNearestRefreshRate(rates, previousSelection) ?? rates.FirstOrDefault();
        RefreshRateStatusText = "仅列出所选分辨率下支持的刷新率，按从高到低排列。";
        RaiseRefreshRateCommandState();
    }

    private static DisplayMode? FindNearestRefreshRate(
        IEnumerable<DisplayMode> modes,
        DisplayMode? preferredMode)
    {
        if (preferredMode is null) return null;
        var preferredRate = preferredMode.RefreshRateNumerator /
                            (double)Math.Max(1, preferredMode.RefreshRateDenominator);
        return modes
            .OrderBy(mode => Math.Abs(mode.RefreshRateNumerator /
                                      (double)Math.Max(1, mode.RefreshRateDenominator) - preferredRate))
            .ThenByDescending(mode => mode.RefreshRateNumerator /
                                      (double)Math.Max(1, mode.RefreshRateDenominator))
            .FirstOrDefault();
    }

    private async Task ApplyResolutionAsync()
    {
        var selected = SelectedResolutionMode;
        if (selected is null) return;
        var current = _availableDisplayModes.FirstOrDefault(mode => mode.IsCurrent);
        var preferredRefreshRate = SelectedRefreshRateMode ?? current;
        var mode = current is null
            ? selected
            : _availableDisplayModes.FirstOrDefault(candidate =>
                candidate.Width == selected.Width && candidate.Height == selected.Height &&
                candidate.BitsPerPixel == selected.BitsPerPixel && HasSameRefreshRate(candidate, current) &&
                candidate.ScanLineOrdering == current.ScanLineOrdering && candidate.Orientation == current.Orientation);
        if (mode is null)
        {
            SetDisplayModeError(isResolution: true, "系统未报告该分辨率在当前刷新率下可用；请先选择一个该分辨率支持的刷新率。");
            return;
        }
        await ApplyDisplayModeTrialAsync(mode, isResolution: true, preferredRefreshRate);
    }

    private async Task ApplyDisplayModeTrialAsync(
        DisplayMode mode,
        bool isResolution,
        DisplayMode? preferredRefreshRate = null)
    {
        var monitor = SelectedMonitor;
        if (_refreshRateTrialCoordinator is null || monitor is null ||
            (isResolution ? !CanSetResolution : !CanSetRefreshRate) || mode.IsCurrent)
            return;

        SetDisplayModeError(isResolution, null);
        SetDisplayModeStatus(isResolution, isResolution
            ? $"正在切换到 {DescribeResolution(mode)}（保持当前刷新率）…"
            : $"正在切换到 {DescribeRefreshRate(mode)}…");
        var changeKindForLog = isResolution ? DisplayModeChangeKind.Resolution : DisplayModeChangeKind.RefreshRate;
        DiagnosticLog.Write("INFO", "display.mode.apply.started", $"kind={changeKindForLog} modeId={mode.ModeId} width={mode.Width} height={mode.Height} refreshHz={mode.RefreshRateHz}");
        try
        {
            var changeKind = changeKindForLog;
            var trial = await _refreshRateTrialCoordinator.StartTrialAsync(
                monitor.Id, mode, TimeSpan.FromSeconds(15), changeKind);
            DiagnosticLog.Write("INFO", "display.mode.trial.started", $"kind={changeKind} previous={trial.Trial.PreviousMode.Width}x{trial.Trial.PreviousMode.Height}@{trial.Trial.PreviousMode.RefreshRateHz} requested={trial.Trial.RequestedMode.Width}x{trial.Trial.RequestedMode.Height}@{trial.Trial.RequestedMode.RefreshRateHz}");
            _activeRefreshRateTrialId = trial.Trial.TrialId;
            RaiseRefreshRateCommandState();

            var keep = ConfirmDisplayModeTrialAsync is not null && await ConfirmDisplayModeTrialAsync(trial);
            var state = keep
                ? await _refreshRateTrialCoordinator.ConfirmAsync(trial.Trial.TrialId)
                : await _refreshRateTrialCoordinator.CancelAsync(trial.Trial.TrialId);

            if (state.Status == RefreshRateTrialStatus.Confirmed)
            {
                DiagnosticLog.Write("INFO", "display.mode.trial.confirmed", $"kind={changeKind} mode={state.Trial.RequestedMode.Width}x{state.Trial.RequestedMode.Height}@{state.Trial.RequestedMode.RefreshRateHz}");
                _restoreFailureTrialId = null;
                SetDisplayModeStatus(isResolution, isResolution
                    ? $"已切换到 {DescribeResolution(state.Trial.RequestedMode)}。"
                    : $"已切换到 {DescribeRefreshRate(state.Trial.RequestedMode)}。");
                await ReloadCurrentDisplayModesAsync(monitor, preferredRefreshRate);
            }
            else if (state.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                DiagnosticLog.Write("ERROR", "display.mode.trial.restore_failed", $"kind={changeKind} message={state.LastError ?? "unknown"}");
                _restoreFailureTrialId = state.Trial.TrialId;
                SetDisplayModeError(isResolution, state.LastError ?? "恢复显示模式失败。");
                SetDisplayModeStatus(isResolution, isResolution
                    ? "恢复原分辨率失败；可重试恢复。"
                    : "恢复原刷新率失败；可重试恢复。");
            }
            else
            {
                DiagnosticLog.Write("INFO", "display.mode.trial.restored", $"kind={changeKind} mode={state.Trial.PreviousMode.Width}x{state.Trial.PreviousMode.Height}@{state.Trial.PreviousMode.RefreshRateHz}");
                _restoreFailureTrialId = null;
                SetDisplayModeStatus(isResolution, isResolution
                    ? $"已恢复原分辨率 {DescribeResolution(state.Trial.PreviousMode)}。"
                    : $"已恢复原刷新率 {DescribeRefreshRate(state.Trial.PreviousMode)}。");
                await ReloadCurrentDisplayModesAsync(monitor);
            }
        }
        catch (Exception exception)
        {
            var platformCode = exception is BackendOperationException backendException ? backendException.PlatformCode : null;
            DiagnosticLog.Write("ERROR", "display.mode.apply.failed", $"kind={changeKindForLog} errorType={exception.GetType().Name} platformCode={platformCode} message={exception.Message}");
            SetDisplayModeError(isResolution, exception.Message);
            SetDisplayModeStatus(isResolution, isResolution
                ? "分辨率试用失败；若模式已更改，超时回滚仍会继续。"
                : "刷新率试用失败；若模式已更改，超时回滚仍会继续。");
        }
        finally
        {
            _activeRefreshRateTrialId = null;
            OnPropertyChanged(nameof(HasRefreshRateRestoreFailure));
            RaiseRefreshRateCommandState();
        }
    }

    private async Task ReloadCurrentDisplayModesAsync(
        MonitorDescriptor monitor,
        DisplayMode? preferredRefreshRate = null)
    {
        if (SelectedMonitor?.Id == monitor.Id)
        {
            await LoadRefreshRateModesAsync(monitor, Volatile.Read(ref _loadVersion));
            if (preferredRefreshRate is not null && SelectedResolutionMode is { } resolution)
            {
                var preferred = FindNearestRefreshRate(RefreshRateModes, preferredRefreshRate);
                if (preferred is not null) SelectedRefreshRateMode = preferred;
            }
        }
    }

    private async Task RetryRefreshRateRestoreAsync()
    {
        if (_refreshRateTrialCoordinator is null || _restoreFailureTrialId is null) return;
        var trialId = _restoreFailureTrialId;
        var isResolution = _refreshRateTrialCoordinator.GetState(trialId)?.Trial.ChangeKind == DisplayModeChangeKind.Resolution;
        SetDisplayModeError(isResolution, null);
        SetDisplayModeStatus(isResolution, isResolution ? "正在重试恢复原分辨率…" : "正在重试恢复原刷新率…");
        try
        {
            var state = await _refreshRateTrialCoordinator.RetryRestoreAsync(trialId);
            if (state.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                SetDisplayModeError(isResolution, state.LastError ?? "恢复仍然失败。");
                SetDisplayModeStatus(isResolution, "恢复仍然失败；可再次重试。");
            }
            else
            {
                _restoreFailureTrialId = null;
                SetDisplayModeStatus(isResolution, isResolution
                    ? $"已恢复原分辨率 {DescribeResolution(state.Trial.PreviousMode)}。"
                    : $"已恢复原刷新率 {DescribeRefreshRate(state.Trial.PreviousMode)}。");
                if (SelectedMonitor is { } monitor && monitor.Id == state.Trial.MonitorId)
                {
                    await ReloadCurrentDisplayModesAsync(monitor);
                }
            }
        }
        catch (Exception exception)
        {
            SetDisplayModeError(isResolution, exception.Message);
            SetDisplayModeStatus(isResolution, "重试恢复失败。");
        }
        finally
        {
            OnPropertyChanged(nameof(HasRefreshRateRestoreFailure));
            if (RetryRefreshRateRestoreCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }

    private bool CanApplyRefreshRate() => CanSetRefreshRate &&
        SelectedRefreshRateMode is { IsCurrent: false } &&
        _refreshRateTrialCoordinator is not null &&
        _activeRefreshRateTrialId is null &&
        !IsLoadingRefreshRates;

    private bool CanApplyResolution() => CanSetResolution &&
        SelectedResolutionMode is { IsCurrent: false } &&
        _refreshRateTrialCoordinator is not null &&
        _activeRefreshRateTrialId is null &&
        !IsLoadingRefreshRates;

    private void RaiseRefreshRateCommandState()
    {
        if (ApplyRefreshRateCommand is AsyncCommand apply) apply.RaiseCanExecuteChanged();
        if (ApplyResolutionCommand is AsyncCommand applyResolution) applyResolution.RaiseCanExecuteChanged();
        if (RetryRefreshRateRestoreCommand is AsyncCommand retry) retry.RaiseCanExecuteChanged();
    }

    private void ClearRefreshRateModes()
    {
        IsLoadingRefreshRates = false;
        RefreshRateModes.Clear();
        ResolutionModes.Clear();
        SelectedRefreshRateMode = null;
        SelectedResolutionMode = null;
        CanSetRefreshRate = false;
        CanSetResolution = false;
        IsHdrSupported = false;
        IsHdrEnabled = false;
        HdrStatus = "请先选择显示器。";
        RefreshRateError = null;
        ResolutionError = null;
        RefreshRateStatusText = _displaySettingsBackend is null ? "" : "请先选择显示器。";
        ResolutionStatusText = _displaySettingsBackend is null ? "" : "请先选择显示器。";
        OnPropertyChanged(nameof(HasRefreshRateModes));
        OnPropertyChanged(nameof(HasNoRefreshRateModes));
        OnPropertyChanged(nameof(HasResolutionModes));
        OnPropertyChanged(nameof(HasNoResolutionModes));
    }

    public static string DescribeRefreshRate(DisplayMode mode) =>
        $"{mode.RefreshRateNumerator / (double)Math.Max(1, mode.RefreshRateDenominator):0.###} Hz";

    public static string DescribeResolution(DisplayMode mode) => $"{mode.Width} × {mode.Height}";

    private static bool HasSameRefreshRate(DisplayMode left, DisplayMode right) =>
        (ulong)left.RefreshRateNumerator * right.RefreshRateDenominator ==
        (ulong)right.RefreshRateNumerator * left.RefreshRateDenominator;

    private void SetDisplayModeError(bool isResolution, string? message)
    {
        if (isResolution) ResolutionError = message;
        else RefreshRateError = message;
    }

    private void SetDisplayModeStatus(bool isResolution, string message)
    {
        if (isResolution) ResolutionStatusText = message;
        else RefreshRateStatusText = message;
    }

    internal async Task ApplyDiscreteAsync(VcpControlViewModel control)
    {
        var monitor = SelectedMonitor;
        var option = control.SelectedOption;
        if (monitor is null || monitor.Id != control.MonitorId || option is null || !control.IsReadable) return;

        if (control.Code == 0x60 && ConfirmInputSourceAsync is not null &&
            !await ConfirmInputSourceAsync(control))
        {
            control.RestoreSelectedValue();
            StatusText = "已取消输入源切换。";
            return;
        }

        await WriteAndVerifyAsync(monitor, control, option.Value);
    }

    internal async Task ApplyContinuousAsync(VcpControlViewModel control, uint value)
    {
        var monitor = SelectedMonitor;
        if (monitor is null || monitor.Id != control.MonitorId || !control.IsReadable) return;
        await WriteAndVerifyAsync(monitor, control, value);
    }

}
