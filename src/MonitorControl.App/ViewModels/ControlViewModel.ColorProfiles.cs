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
    private string? GetSelectedDisplayName(MonitorDescriptor? monitor) =>
        string.IsNullOrWhiteSpace(monitor?.ConnectionDescription) ? null : monitor.ConnectionDescription;

    private async Task LoadDisplayColorProfilesAsync()
    {
        if (_displayColorProfileService?.IsSupported != true || SelectedMonitor is not { } monitor || GetSelectedDisplayName(monitor) is not { } displayName)
        {
            DisplayColorProfileStatus = "此 Windows 版本或当前显示器不支持 ICC profile 管理。";
            return;
        }
        try
        {
            var active = await _displayColorProfileService.GetActiveProfilePathAsync(displayName);
            var profiles = await _displayColorProfileService.GetProfilesAsync(displayName);
            DisplayColorProfiles.Clear();
            foreach (var profile in profiles) DisplayColorProfiles.Add(profile);
            SelectedDisplayColorProfile = DisplayColorProfiles.FirstOrDefault(profile => string.Equals(profile.Path, active, StringComparison.OrdinalIgnoreCase)) ?? DisplayColorProfiles.FirstOrDefault();
            HasOriginalDisplayColorProfile = await _displayColorProfileService.HasOriginalProfileAsync(displayName);
            DisplayColorProfileStatus = profiles.Count == 0 ? "此显示器没有可选择的已关联 ICC 配置文件。" : $"已读取 {profiles.Count} 个配置文件。当前：{Path.GetFileName(active)}";
            await LoadDisplayColorPipelineStatusAsync(displayName, monitor);
        }
        catch (Exception exception) { DisplayColorProfileStatus = $"读取显示器 ICC 配置文件失败：{exception.Message}"; }
    }

    private async Task LoadDisplayColorPipelineStatusAsync(string displayName, MonitorDescriptor monitor)
    {
        if (_displayColorProfileService is null)
        {
            DisplayColorPipelineStatus = "当前平台未提供 Windows 色彩管线能力查询。";
            return;
        }

        try
        {
            var capabilities = await _displayColorProfileService.GetPipelineCapabilitiesAsync(displayName);
            _supportsVcgtAdjustment = capabilities.VcgtQuerySucceeded && capabilities.SupportsVcgt;
            _calibrationLoaderEnabled = capabilities.CalibrationManagementQuerySucceeded && capabilities.IsCalibrationManagementEnabled;
            if (ApplyRgbAdjustmentCommand is AsyncCommand rgbCommand) rgbCommand.RaiseCanExecuteChanged();
            var vcgt = capabilities.VcgtQuerySucceeded
                ? capabilities.SupportsVcgt ? "支持" : "不支持"
                : $"查询失败 (0x{capabilities.VcgtHResult:X8})";
            var mhc2 = capabilities.Mhc2QuerySucceeded
                ? capabilities.SupportsMhc2
                    ? $"支持（LUT {capabilities.RegammaLutEntryCount} 项，矩阵 {capabilities.CscXyzMatrixRows}×{capabilities.CscXyzMatrixColumns}）"
                    : "不支持"
                : $"查询失败 (0x{capabilities.Mhc2HResult:X8})";
            var calibrationManagement = capabilities.CalibrationManagementQuerySucceeded
                ? capabilities.IsCalibrationManagementEnabled ? "已开启" : "已关闭"
                : $"查询失败 (Win32 {capabilities.CalibrationManagementWin32Error})";
            var hdr = "未知";
            if (_displaySettingsBackend is not null)
            {
                var hdrState = await _displaySettingsBackend.GetHdrStateAsync(monitor.Id);
                hdr = hdrState.IsSupported ? hdrState.IsEnabled ? "已开启" : "已关闭" : "不支持或无法读取";
            }

            DisplayColorPipelineStatus = $"只读能力：VCGT {vcgt}；系统校准加载器 {calibrationManagement}；MHC2 {mhc2}；HDR {hdr}。能力支持不代表所选 ICC 含有效校准数据或画面已发生变化。";
            DiagnosticLog.Write("INFO", "display.icc.pipeline.displayed", $"display={displayName}; scope={capabilities.Scope}; vcgtQuery={capabilities.VcgtQuerySucceeded}; supportsVcgt={capabilities.SupportsVcgt}; calibrationStateQuery={capabilities.CalibrationManagementQuerySucceeded}; calibrationManagementEnabled={capabilities.IsCalibrationManagementEnabled}; calibrationWin32Error={capabilities.CalibrationManagementWin32Error}; mhc2Query={capabilities.Mhc2QuerySucceeded}; supportsMhc2={capabilities.SupportsMhc2}; hdr={hdr}");
        }
        catch (Exception exception)
        {
            DisplayColorPipelineStatus = $"读取色彩管线能力失败：{exception.Message}";
            DiagnosticLog.Write("WARN", "display.icc.pipeline.failed", $"display={displayName}; error={exception}");
        }
    }

    private async Task InspectSelectedDisplayColorProfileAsync(DisplayColorProfile? profile)
    {
        if (profile is null)
        {
            DisplayColorProfileInspectionStatus = "选择配置文件后，可查看 ICC 文件头和校准标签摘要。";
            return;
        }

        DisplayColorProfileInspectionStatus = "正在只读检查 ICC 文件…";
        try
        {
            var inspection = await Task.Run(() => IccProfileInspector.Inspect(profile.Path));
            if (!ReferenceEquals(SelectedDisplayColorProfile, profile)) return;
            if (profile.IsDefault)
            {
                _hasKnownAppliedRgbGains = inspection.RgbGainAdjustment is not null;
                if (inspection.RgbGainAdjustment is { } applied)
                {
                    _appliedRedRgbGainPercent = applied.RedPercent;
                    _appliedGreenRgbGainPercent = applied.GreenPercent;
                    _appliedBlueRgbGainPercent = applied.BluePercent;
                    RedRgbGainPercent = applied.RedPercent;
                    GreenRgbGainPercent = applied.GreenPercent;
                    BlueRgbGainPercent = applied.BluePercent;
                    RgbGainStatus = $"当前活动 profile 已应用 RGB：R {applied.RedPercent}% / G {applied.GreenPercent}% / B {applied.BluePercent}%。";
                }
                else RgbGainStatus = "当前 profile 不是由本应用生成，无法从 ICC 通用数据还原其 RGB 参数。";
                OnPropertyChanged(nameof(IsRgbAdjustmentPending));
                OnPropertyChanged(nameof(RgbGainPendingStatus));
            }
            static string TagSummary(IccProfileTag? tag) => tag is null ? "无" : $"有（{tag.Size:N0} 字节）";
            var vcgt = inspection.VcgtSummary;
            var vcgtStructure = vcgt is null
                ? "无"
                : !vcgt.IsStructurallyValid
                    ? $"结构异常：{vcgt.ValidationMessage}"
                    : vcgt.Encoding == "Formula"
                        ? "公式型，3 通道"
                        : $"采样表，{vcgt.ChannelCount} 通道，{vcgt.SamplesPerChannel} 点/通道，{vcgt.EntryBits} 位";
            var mhc2 = inspection.Mhc2Summary;
            var mhc2Structure = mhc2 is null
                ? "无"
                : !mhc2.IsStructurallyValid
                    ? $"结构异常：{mhc2.ValidationMessage}"
                    : $"{mhc2.LutEntryCount} 点，矩阵 {(mhc2.HasMatrix ? "有" : "无")}，RGB LUT {(mhc2.HasRedLut ? "有" : "无")}/{(mhc2.HasGreenLut ? "有" : "无")}/{(mhc2.HasBlueLut ? "有" : "无")}";
            DisplayColorProfileInspectionStatus =
                $"{Path.GetFileName(inspection.FilePath)} · {inspection.ProfileClass} · {inspection.ColorSpace} → {inspection.ConnectionSpace} · {inspection.DeclaredSize:N0} 字节\n" +
                $"VCGT：{TagSummary(inspection.VcgtTag)}；结构：{vcgtStructure}　MS00：{TagSummary(inspection.Ms00Tag)}　MHC2：{TagSummary(inspection.Mhc2Tag)}；结构：{mhc2Structure}";
            DiagnosticLog.Write("INFO", "display.icc.inspection.completed", $"profile={inspection.FilePath}; bytes={inspection.DeclaredSize}; class={inspection.ProfileClass}; colorSpace={inspection.ColorSpace}; pcs={inspection.ConnectionSpace}; vcgt={inspection.VcgtTag?.Size}; vcgtEncoding={vcgt?.Encoding}; vcgtChannels={vcgt?.ChannelCount}; vcgtSamples={vcgt?.SamplesPerChannel}; vcgtEntryBits={vcgt?.EntryBits}; vcgtValid={vcgt?.IsStructurallyValid}; vcgtValidation={vcgt?.ValidationMessage}; ms00={inspection.Ms00Tag?.Size}; mhc2={inspection.Mhc2Tag?.Size}; mhc2LutEntries={mhc2?.LutEntryCount}; mhc2Matrix={mhc2?.HasMatrix}; mhc2RedLut={mhc2?.HasRedLut}; mhc2GreenLut={mhc2?.HasGreenLut}; mhc2BlueLut={mhc2?.HasBlueLut}; mhc2Valid={mhc2?.IsStructurallyValid}; mhc2Validation={mhc2?.ValidationMessage}");
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(SelectedDisplayColorProfile, profile)) return;
            DisplayColorProfileInspectionStatus = $"ICC 文件只读检查失败：{exception.Message}";
            DiagnosticLog.Write("WARN", "display.icc.inspection.failed", $"profile={profile.Path}; error={exception}");
        }
    }

    private async Task ApplyDisplayColorProfileAsync()
    {
        if (_displayColorProfileService is null || SelectedMonitor is not { } monitor || SelectedDisplayColorProfile is not { } profile || GetSelectedDisplayName(monitor) is not { } displayName) return;
        try
        {
            CancelRgbPreviewDebounce();
            await RestorePendingRgbPreviewAsync();
            DisplayColorProfileStatus = $"正在应用“{profile.Name}”…";
            await _displayColorProfileService.ApplyProfileAsync(displayName, profile.Path);
            HasOriginalDisplayColorProfile = await _displayColorProfileService.HasOriginalProfileAsync(displayName);
            await LoadDisplayColorProfilesAsync();
            DisplayColorProfileStatus = $"已应用“{profile.Name}”。画面效果取决于 Windows 颜色管线和应用支持。";
            DiagnosticLog.Write("INFO", "display.icc.apply.completed", $"display={displayName}; profile={profile.Path}");
        }
        catch (Exception exception)
        {
            DisplayColorProfileStatus = $"应用 ICC 配置文件失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.apply.failed", $"error={exception}");
        }
    }

    private bool CanApplyRgbAdjustment() => _displayColorProfileService?.IsSupported == true &&
        SelectedMonitor is not null && !IsHdrEnabled && _supportsVcgtAdjustment && _calibrationLoaderEnabled;

    private void OnRgbGainChanged()
    {
        OnPropertyChanged(nameof(RedRgbGainLabel));
        OnPropertyChanged(nameof(GreenRgbGainLabel));
        OnPropertyChanged(nameof(BlueRgbGainLabel));
        OnPropertyChanged(nameof(IsRgbAdjustmentPending));
        OnPropertyChanged(nameof(RgbGainPendingStatus));
        if (ApplyRgbAdjustmentCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        ScheduleRgbPreview();
    }

    private void ScheduleRgbPreview()
    {
        CancelRgbPreviewDebounce();
        if (_displayColorProfileService?.IsSupported != true || SelectedMonitor is null || IsHdrEnabled) return;
        var debounce = new CancellationTokenSource();
        _rgbPreviewDebounce = debounce;
        _ = PreviewRgbAdjustmentAfterDelayAsync(debounce);
    }

    private void CancelRgbPreviewDebounce()
    {
        var debounce = _rgbPreviewDebounce;
        _rgbPreviewDebounce = null;
        if (debounce is null) return;
        debounce.Cancel();
    }

    private async Task PreviewRgbAdjustmentAfterDelayAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(350, debounce.Token);
            if (!ReferenceEquals(_rgbPreviewDebounce, debounce) || SelectedMonitor is not { } monitor ||
                GetSelectedDisplayName(monitor) is not { } displayName || _displayColorProfileService is null) return;
            if (_displaySettingsBackend is not null)
            {
                var hdr = await _displaySettingsBackend.GetHdrStateAsync(monitor.Id);
                IsHdrEnabled = hdr.IsSupported && hdr.IsEnabled;
                if (IsHdrEnabled) return;
            }
            var gains = new RgbGainAdjustment(RedRgbGainPercent, GreenRgbGainPercent, BlueRgbGainPercent);
            gains.Validate();
            if (_rgbPreviewDisplayName is not null && !string.Equals(_rgbPreviewDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                await RestoreRgbPreviewForDisplayAsync(_rgbPreviewDisplayName);
            RgbGainStatus = "正在预览 RGB 调整（临时 Gamma Ramp）…";
            DiagnosticLog.Write("INFO", "display.icc.rgb.preview.started", $"display={displayName}; red={gains.RedPercent}; green={gains.GreenPercent}; blue={gains.BluePercent}");
            await _displayColorProfileService.PreviewRgbAdjustmentAsync(displayName, gains, debounce.Token);
            if (!ReferenceEquals(_rgbPreviewDebounce, debounce) || SelectedMonitor is not { } currentMonitor ||
                !string.Equals(GetSelectedDisplayName(currentMonitor), displayName, StringComparison.OrdinalIgnoreCase))
            {
                await RestoreRgbPreviewForDisplayAsync(displayName);
                return;
            }
            _rgbPreviewDisplayName = gains == RgbGainAdjustment.Neutral ? null : displayName;
            RgbGainStatus = $"临时预览：R {gains.RedPercent}% / G {gains.GreenPercent}% / B {gains.BluePercent}%。点击“应用 RGB 调整”可保存为 ICC profile。";
            DiagnosticLog.Write("INFO", "display.icc.rgb.preview.completed", $"display={displayName}; red={gains.RedPercent}; green={gains.GreenPercent}; blue={gains.BluePercent}; readback=true");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            RgbGainStatus = $"RGB 临时预览不可用：{exception.Message}";
            DiagnosticLog.Write("WARN", "display.icc.rgb.preview.failed", $"error={exception}");
        }
        finally
        {
            if (ReferenceEquals(_rgbPreviewDebounce, debounce)) _rgbPreviewDebounce = null;
            debounce.Dispose();
        }
    }

    public bool HasPendingRgbPreview => _rgbPreviewDisplayName is not null;

    public async Task<bool> RestoreRgbPreviewBeforeExitAsync()
    {
        CancelRgbPreviewDebounce();
        if (_rgbPreviewDisplayName is { } displayName)
            await RestoreRgbPreviewForDisplayAsync(displayName);
        return _rgbPreviewDisplayName is null;
    }

    private async Task RestorePendingRgbPreviewAsync()
    {
        await RestoreRgbPreviewBeforeExitAsync();
    }

    private async Task RestoreRgbPreviewForDisplayAsync(string displayName)
    {
        if (_displayColorProfileService is null) return;
        try
        {
            await _displayColorProfileService.RestoreRgbPreviewAsync(displayName);
            if (string.Equals(_rgbPreviewDisplayName, displayName, StringComparison.OrdinalIgnoreCase)) _rgbPreviewDisplayName = null;
            DiagnosticLog.Write("INFO", "display.icc.rgb.preview.restored", $"display={displayName}");
        }
        catch (Exception exception)
        {
            RgbGainStatus = $"恢复临时 RGB 预览失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.rgb.preview.restore_failed", $"display={displayName}; error={exception}");
        }
    }

    private void ResetRgbAdjustment()
    {
        RedRgbGainPercent = 100;
        GreenRgbGainPercent = 100;
        BlueRgbGainPercent = 100;
    }

    private async Task ApplyRgbAdjustmentAsync()
    {
        if (_displayColorProfileService is null || SelectedMonitor is not { } monitor ||
            GetSelectedDisplayName(monitor) is not { } displayName) return;
        if (_displaySettingsBackend is not null)
        {
            var hdrState = await _displaySettingsBackend.GetHdrStateAsync(monitor.Id);
            IsHdrEnabled = hdrState.IsSupported && hdrState.IsEnabled;
            if (IsHdrEnabled)
            {
                RgbGainStatus = "当前 HDR 已开启；SDR VCGT RGB 校准不适用于 HDR，请关闭 HDR 后再应用。";
                return;
            }
        }

        try
        {
            var gains = new RgbGainAdjustment(RedRgbGainPercent, GreenRgbGainPercent, BlueRgbGainPercent);
            gains.Validate();
            if (PromptRgbProfileFileNameAsync is null)
            {
                RgbGainStatus = "保存对话框尚未初始化，请关闭并重新打开设置页后重试。";
                return;
            }
            var defaultFileName = await _displayColorProfileService.GetDefaultRgbProfileFileNameAsync(displayName, gains);
            var profileFileName = await PromptRgbProfileFileNameAsync(defaultFileName);
            if (string.IsNullOrWhiteSpace(profileFileName)) return;

            CancelRgbPreviewDebounce();
            await RestorePendingRgbPreviewAsync();
            RgbGainStatus = "正在生成并应用 SDR RGB 校准 profile…";
            DiagnosticLog.Write("INFO", "display.icc.rgb.apply.started", $"display={displayName}; fileName={profileFileName}; red={gains.RedPercent}; green={gains.GreenPercent}; blue={gains.BluePercent}");
            var profilePath = await _displayColorProfileService.ApplyRgbAdjustmentAsync(displayName, gains, profileFileName);
            _appliedRedRgbGainPercent = gains.RedPercent;
            _appliedGreenRgbGainPercent = gains.GreenPercent;
            _appliedBlueRgbGainPercent = gains.BluePercent;
            _hasKnownAppliedRgbGains = true;
            OnPropertyChanged(nameof(IsRgbAdjustmentPending));
            OnPropertyChanged(nameof(RgbGainPendingStatus));
            RgbGainStatus = $"已应用 RGB 校准：R {gains.RedPercent}% / G {gains.GreenPercent}% / B {gains.BluePercent}%。Windows 已读回活动 profile；画面效果需实机确认。";
            HasOriginalDisplayColorProfile = await _displayColorProfileService.HasOriginalProfileAsync(displayName);
            await LoadDisplayColorProfilesAsync();
            DiagnosticLog.Write("INFO", "display.icc.rgb.apply.readback", $"display={displayName}; profile={profilePath}; red={gains.RedPercent}; green={gains.GreenPercent}; blue={gains.BluePercent}");
        }
        catch (Exception exception)
        {
            RgbGainStatus = $"RGB 校准应用失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.rgb.apply.failed", $"display={displayName}; error={exception}");
            if (_displayColorProfileService is not null)
            {
                try
                {
                    await _displayColorProfileService.PreviewRgbAdjustmentAsync(displayName,
                        new RgbGainAdjustment(RedRgbGainPercent, GreenRgbGainPercent, BlueRgbGainPercent));
                    _rgbPreviewDisplayName = displayName;
                }
                catch (Exception previewException)
                {
                    DiagnosticLog.Write("WARN", "display.icc.rgb.preview.restore_after_apply_failure", $"display={displayName}; error={previewException}");
                }
            }
        }
    }

    private async Task ImportDisplayColorProfileAsync()
    {
        if (_displayColorProfileService is null)
        {
            DisplayColorProfileStatus = "当前平台未提供 ICC 配置文件导入功能。";
            DiagnosticLog.Write("ERROR", "display.icc.import.unavailable", "service=missing");
            return;
        }
        if (PickDisplayColorProfileFileAsync is null)
        {
            DisplayColorProfileStatus = "文件选择器尚未初始化，请关闭并重新打开设置页后重试。";
            DiagnosticLog.Write("ERROR", "display.icc.import.unavailable", "filePicker=missing");
            return;
        }
        if (SelectedMonitor is not { } monitor || GetSelectedDisplayName(monitor) is not { } displayName)
        {
            DisplayColorProfileStatus = "请先扫描并选择一个显示器，再导入 ICC 配置文件。";
            DiagnosticLog.Write("WARN", "display.icc.import.unavailable", "display=missing");
            return;
        }
        try
        {
            DiagnosticLog.Write("INFO", "display.icc.import.started", $"display={displayName}");
            var sourcePath = await PickDisplayColorProfileFileAsync();
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                DiagnosticLog.Write("INFO", "display.icc.import.cancelled", $"display={displayName}");
                return;
            }
            DiagnosticLog.Write("INFO", "display.icc.import.file.selected", $"display={displayName}; source={sourcePath}");
            DisplayColorProfileStatus = "正在安装并关联 ICC 配置文件…";
            var installedPath = await _displayColorProfileService.ImportProfileAsync(displayName, sourcePath);
            DiagnosticLog.Write("INFO", "display.icc.import.installed", $"display={displayName}; profile={installedPath}");
            await LoadDisplayColorProfilesAsync();
            SelectedDisplayColorProfile = DisplayColorProfiles.FirstOrDefault(profile => string.Equals(profile.Path, installedPath, StringComparison.OrdinalIgnoreCase));
            DisplayColorProfileStatus = $"已导入并关联“{Path.GetFileName(sourcePath)}”。它尚未设为默认；如需应用，请点击“应用配置文件”。";
            DiagnosticLog.Write("INFO", "display.icc.import.completed", $"display={displayName}; profile={installedPath}");
        }
        catch (Exception exception)
        {
            DisplayColorProfileStatus = $"导入 ICC 配置文件失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.import.failed", $"error={exception}");
        }
    }

    private async Task RestoreDisplayColorProfileAsync()
    {
        if (_displayColorProfileService is null || SelectedMonitor is not { } monitor || GetSelectedDisplayName(monitor) is not { } displayName) return;
        try
        {
            CancelRgbPreviewDebounce();
            await RestorePendingRgbPreviewAsync();
            DisplayColorProfileStatus = "正在恢复原 ICC 配置文件…";
            await _displayColorProfileService.RestoreOriginalProfileAsync(displayName);
            HasOriginalDisplayColorProfile = false;
            await LoadDisplayColorProfilesAsync();
            DisplayColorProfileStatus = "已恢复并验证原 ICC 配置文件。";
            DiagnosticLog.Write("INFO", "display.icc.restore.completed", $"display={displayName}");
        }
        catch (Exception exception)
        {
            DisplayColorProfileStatus = $"恢复 ICC 配置文件失败，可重试：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.restore.failed", $"error={exception}");
        }
    }

    private async Task DeleteGeneratedRgbProfileAsync()
    {
        if (!CanDeleteSelectedGeneratedRgbProfile || _displayColorProfileService is null ||
            SelectedMonitor is not { } monitor || SelectedDisplayColorProfile is not { } profile ||
            GetSelectedDisplayName(monitor) is not { } displayName) return;
        if (ConfirmGeneratedRgbProfileDeletionAsync is null || !await ConfirmGeneratedRgbProfileDeletionAsync(profile.Name)) return;
        try
        {
            DisplayColorProfileStatus = $"正在删除 RGB profile“{profile.Name}”…";
            DiagnosticLog.Write("INFO", "display.icc.rgb.profile.delete.started", $"display={displayName}; profile={profile.Path}");
            await _displayColorProfileService.DeleteGeneratedRgbProfileAsync(displayName, profile.Path);
            SelectedDisplayColorProfile = null;
            await LoadDisplayColorProfilesAsync();
            DisplayColorProfileStatus = $"已删除 RGB profile“{profile.Name}”。";
            DiagnosticLog.Write("INFO", "display.icc.rgb.profile.delete.completed", $"display={displayName}; profile={profile.Path}");
        }
        catch (Exception exception)
        {
            DisplayColorProfileStatus = $"删除 RGB profile 失败：{exception.Message}";
            DiagnosticLog.Write("ERROR", "display.icc.rgb.profile.delete.failed", $"display={displayName}; profile={profile.Path}; error={exception}");
        }
    }
}
