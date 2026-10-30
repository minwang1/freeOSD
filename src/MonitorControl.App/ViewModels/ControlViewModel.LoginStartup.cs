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
    private async Task LoadLoginStartupAsync()
    {
        if (_loginStartupService is null || !_loginStartupService.IsSupported)
        {
            LoginStartupStatus = "此平台暂不支持登录启动。";
            return;
        }
        try
        {
            _loginStartupEnabled = await _loginStartupService.GetEnabledAsync();
            OnPropertyChanged(nameof(LoginStartupEnabled));
            LoginStartupStatus = "仅为当前 Windows 用户设置，无需管理员权限。";
        }
        catch (Exception exception) { LoginStartupStatus = $"读取登录启动状态失败：{exception.Message}"; }
    }

    private async Task UpdateLoginStartupAsync(bool enabled)
    {
        try
        {
            await _loginStartupService!.SetEnabledAsync(enabled);
            LoginStartupStatus = enabled ? "已启用登录时启动。" : "已关闭登录时启动。";
        }
        catch (Exception exception)
        {
            _loginStartupEnabled = !enabled;
            OnPropertyChanged(nameof(LoginStartupEnabled));
            LoginStartupStatus = $"更新登录启动设置失败：{exception.Message}";
        }
    }

}
