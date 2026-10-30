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
    private async Task LoadProfilesAsync()
    {
        IsLoadingProfiles = true;
        try
        {
            var profiles = await _profileStore.LoadAllAsync();
            Profiles.Clear();
            foreach (var profile in profiles) Profiles.Add(profile);
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(HasNoProfiles));
            if (DefaultAutomationProfile is null && _pendingDefaultProfileName is not null)
                DefaultAutomationProfile = Profiles.FirstOrDefault(item => string.Equals(item.Name, _pendingDefaultProfileName, StringComparison.OrdinalIgnoreCase));
            RaiseAutomationCommands();
            ProfileStatus = $"已加载 {Profiles.Count} 个预设。";
        }
        catch (Exception exception) { ProfileStatus = $"读取预设失败：{exception.Message}"; }
        finally { IsLoadingProfiles = false; RaiseProfileCommands(); }
    }

    private async Task SaveProfileAsync()
    {
        var monitor = SelectedMonitor;
        if (monitor is null || string.IsNullOrWhiteSpace(ProfileName)) return;
        var values = new Dictionary<byte, uint>();
        foreach (var control in ContinuousControls.Concat(DiscreteControls))
            if (control.TryGetConfirmedValue(out var value)) values[control.Code] = value;
        if (values.Count == 0) { ProfileStatus = "没有可保存的已确认控件值。"; return; }
        try
        {
            var profile = new MonitorProfile(ProfileStore.CurrentSchemaVersion, ProfileName.Trim(), DateTimeOffset.UtcNow,
                monitor.Id.Value, values, new Dictionary<string, IdentifiedControlMetadata>());
            await _profileStore.SaveAsync(profile);
            ProfileName = "";
            await LoadProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(item => string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            ProfileStatus = $"已保存预设“{profile.Name}”，包含 {values.Count} 项。";
        }
        catch (Exception exception) { ProfileStatus = $"保存预设失败：{exception.Message}"; }
    }

    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } profile) return;
        try
        {
            await _profileStore.DeleteAsync(profile.Name);
            SelectedProfile = null;
            await LoadProfilesAsync();
            var removedRules = AutomationRules.Where(rule => string.Equals(rule.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var rule in removedRules) AutomationRules.Remove(rule);
            if (_selectedAutomationRule is not null && removedRules.Contains(_selectedAutomationRule)) SelectedAutomationRule = null;
            if (removedRules.Length > 0) await SaveAutomationRulesAsync();
            if (string.Equals(DefaultAutomationProfile?.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                DefaultAutomationProfile = null;
            ProfileStatus = $"已删除预设“{profile.Name}”。";
        }
        catch (Exception exception) { ProfileStatus = $"删除预设失败：{exception.Message}"; }
    }

    private async Task ApplyProfileAsync()
    {
        var monitor = SelectedMonitor;
        var profile = SelectedProfile;
        if (monitor is null || profile is null) return;
        await ApplyProfileValuesAsync(monitor, profile, skipInputSource: false);
    }

    private async Task ApplyProfileValuesAsync(MonitorDescriptor monitor, MonitorProfile profile, bool skipInputSource)
    {
        var controls = ContinuousControls.Concat(DiscreteControls).ToDictionary(control => control.Code);
        var issues = new List<string>();
        var applied = 0;
        foreach (var entry in profile.VcpValues.OrderBy(pair => pair.Key))
        {
            if (skipInputSource && entry.Key == 0x60)
            {
                issues.Add("自动化已跳过输入源 VCP 0x60。请在手动预设中更改输入源。");
                continue;
            }
            if (!controls.TryGetValue(entry.Key, out var control))
            {
                issues.Add($"VCP 0x{entry.Key:X2}: 当前显示器没有该控件。");
                continue;
            }
            try
            {
                await _deviceGate.WaitAsync();
                try
                {
                    await _backend.WriteVcpAsync(monitor.Id, entry.Key, entry.Value);
                    var actual = await _backend.ReadVcpAsync(monitor.Id, entry.Key);
                    if (actual.Current != entry.Value) throw new InvalidOperationException($"读回值为 {actual.Current}，目标值为 {entry.Value}。");
                    control.SetReadValue(actual.Current);
                    applied++;
                }
                finally { _deviceGate.Release(); }
            }
            catch (Exception exception) { issues.Add($"{control.Name} (0x{entry.Key:X2}): {exception.Message}"); }
        }
        var summary = issues.Count == 0
            ? $"已应用预设“{profile.Name}”，共 {applied} 项。"
            : $"预设部分应用：成功 {applied} 项，失败 {issues.Count} 项。\n" + string.Join("\n", issues);
        if (skipInputSource) AutomationStatus = summary;
        else ProfileStatus = summary;
    }

    private async Task LoadAutomationRulesAsync()
    {
        try
        {
            _isLoadingAutomationSettings = true;
            var settings = await _automationRuleStore.LoadAsync();
            AutomationRules.Clear();
            foreach (var rule in settings.Rules) AutomationRules.Add(rule);
            _pendingDefaultProfileName = settings.DefaultProfileName;
            DefaultAutomationProfile = Profiles.FirstOrDefault(item => string.Equals(item.Name, settings.DefaultProfileName, StringComparison.OrdinalIgnoreCase));
            _isAutomationEnabled = settings.IsEnabled && IsAutomationSupported;
            OnPropertyChanged(nameof(IsAutomationEnabled));
            AutomationStatus = IsAutomationSupported
                ? $"已加载 {AutomationRules.Count} 条应用规则。自动切换{(_isAutomationEnabled ? "已启用" : "已关闭")}。"
                : "此平台暂不支持前台应用监控。";
        }
        catch (Exception exception) { AutomationStatus = $"读取自动化规则失败：{exception.Message}"; }
        finally { _isLoadingAutomationSettings = false; }
    }

    private async Task AddAutomationRuleAsync()
    {
        if (AddAutomationRuleDialogAsync is null || Profiles.Count == 0) return;
        var selection = await AddAutomationRuleDialogAsync(Profiles.ToArray(), DefaultAutomationProfile?.Name);
        if (selection is null || string.IsNullOrWhiteSpace(selection.Value.ExecutablePath) || string.IsNullOrWhiteSpace(selection.Value.ProfileName)) return;
        var profile = Profiles.FirstOrDefault(item => string.Equals(item.Name, selection.Value.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        ReplaceAutomationRule(selection.Value.ExecutablePath, profile);
        await SaveAutomationRulesAsync();
        AutomationStatus = $"已将 {Path.GetFileName(selection.Value.ExecutablePath)} 绑定到预设“{profile.Name}”。";
    }

    private void ReplaceAutomationRule(string path, MonitorProfile profile)
    {
        var existing = AutomationRules.FirstOrDefault(rule => string.Equals(rule.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) AutomationRules.Remove(existing);
        var rule = new ApplicationProfileRule(path, profile.Name);
        AutomationRules.Add(rule);
        SelectedAutomationRule = rule;
    }

    private async Task RemoveAutomationRuleAsync()
    {
        if (SelectedAutomationRule is not { } rule) return;
        AutomationRules.Remove(rule);
        SelectedAutomationRule = null;
        await SaveAutomationRulesAsync();
        AutomationStatus = $"已删除应用规则“{rule.ExecutablePath}”。";
    }

    private async Task SaveAutomationRulesAsync()
    {
        if (_isLoadingAutomationSettings) return;
        try { await _automationRuleStore.SaveAsync(new ApplicationAutomationSettings(AutomationRules.ToArray(), DefaultAutomationProfile?.Name, IsAutomationEnabled)); }
        catch (Exception exception) { AutomationStatus = $"保存自动化规则失败：{exception.Message}"; }
    }

    private string? _pendingDefaultProfileName;
    private bool CanAddAutomationRule() => IsAutomationSupported && Profiles.Count > 0;

    private void RaiseAutomationCommands()
    {
        if (AddAutomationRuleCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
    }

    private async Task StartForegroundTrackingAsync()
    {
        try { await _foregroundApplicationMonitor!.StartAsync(); }
        catch (Exception exception) { AutomationStatus = $"无法监听前台程序：{exception.Message}"; }
    }

    private Task SetAutomationEnabledAsync(bool enabled)
    {
        if (_foregroundApplicationMonitor is null)
        {
            AutomationStatus = "此平台暂不支持前台应用监控。";
            _isAutomationEnabled = false;
            OnPropertyChanged(nameof(IsAutomationEnabled));
            return Task.CompletedTask;
        }
        if (enabled)
            AutomationStatus = "前台应用自动切换已启用；优先使用应用规则，未命中时应用默认预设。";
        else
        {
            _lastAutomationKey = null;
            AutomationStatus = "前台应用自动切换已关闭。";
        }
        return Task.CompletedTask;
    }

    private void OnForegroundApplicationChanged(object? sender, ForegroundApplicationChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsAutomationEnabled) return;
            var rule = AutomationRules.FirstOrDefault(candidate => candidate.IsEnabled &&
                string.Equals(candidate.ExecutablePath, eventArgs.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (SelectedMonitor is not { } monitor) return;
            var profileName = rule?.ProfileName ?? DefaultAutomationProfile?.Name;
            if (profileName is null) return;
            var profile = Profiles.FirstOrDefault(candidate => string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                AutomationStatus = $"自动化预设“{profileName}”不存在。";
                return;
            }
            // Unmatched apps share the same fallback rule; changing between them must not reapply it.
            var ruleIdentity = rule?.ExecutablePath ?? $"default:{profile.Name}";
            var key = $"{ruleIdentity}|{profile.Name}|{monitor.Id.Value}";
            if (string.Equals(_lastAutomationKey, key, StringComparison.OrdinalIgnoreCase)) return;
            AutomationStatus = $"检测到 {Path.GetFileName(eventArgs.ExecutablePath)}，正在应用“{profile.Name}”…";
            try
            {
                await ApplyProfileValuesAsync(monitor, profile, skipInputSource: true);
                // Only suppress repeated foreground notifications after a complete apply.
                // A partial failure must remain retryable the next time this app is activated.
                if (AutomationStatus.StartsWith("已应用预设", StringComparison.Ordinal) ||
                    AutomationStatus.StartsWith("预设部分应用：成功", StringComparison.Ordinal) &&
                    AutomationStatus.EndsWith("失败 0 项。", StringComparison.Ordinal))
                    _lastAutomationKey = key;
            }
            catch (Exception exception)
            {
                AutomationStatus = $"自动应用预设“{profile.Name}”失败：{exception.Message}";
                DiagnosticLog.Write("ERROR", "automation.profile.apply.failed", $"profile={profile.Name}; monitor={monitor.Id.Value}; error={exception}");
            }
        });
    }

    private void RaiseProfileCommands()
    {
        if (SaveProfileCommand is AsyncCommand save) save.RaiseCanExecuteChanged();
        if (ApplyProfileCommand is AsyncCommand apply) apply.RaiseCanExecuteChanged();
        if (DeleteProfileCommand is AsyncCommand delete) delete.RaiseCanExecuteChanged();
    }

}
