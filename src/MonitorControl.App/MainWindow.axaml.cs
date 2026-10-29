using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MonitorControl.Core.Services;
using MonitorControl.App.ViewModels;
using MonitorControl.App.Services;

namespace MonitorControl.App;

public partial class MainWindow : Window
{
    private readonly ControlViewModel? _viewModel;
    private readonly WindowSettingsStore _windowSettingsStore = new();
    public bool AllowClose { get; set; }
    public bool TrayAvailable { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ControlViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewModel.ConfirmInputSourceAsync = ConfirmInputSourceAsync;
        _viewModel.ConfirmDisplayModeTrialAsync = ConfirmDisplayModeTrialAsync;
        DataContext = _viewModel;
        Opened += RestoreWindowSettingsAsync;
        Opened += OnOpened;
        Closing += SaveWindowSettings;
        Closing += CloseToTray;
        Closed += OnClosed;
    }

    private void CloseToTray(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (AllowClose || !TrayAvailable) return;
        eventArgs.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    private async void RestoreWindowSettingsAsync(object? sender, EventArgs eventArgs)
    {
        try
        {
            var settings = await _windowSettingsStore.LoadAsync();
            if (settings is null) return;
            Width = Math.Clamp(settings.Width, MinWidth, 4096);
            Height = Math.Clamp(settings.Height, MinHeight, 2160);
            Position = new Avalonia.PixelPoint((int)settings.X, (int)settings.Y);
            if (_viewModel is not null) _viewModel.IsProfilesTab = settings.IsProfilesTab;
        }
        catch { }
    }

    private void SaveWindowSettings(object? sender, WindowClosingEventArgs eventArgs)
    {
        try { _windowSettingsStore.Save(this, _viewModel?.IsProfilesTab ?? false); }
        catch { }
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            await _viewModel.InitializeAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.ConfirmInputSourceAsync = null;
            _viewModel.ConfirmDisplayModeTrialAsync = null;
        }
    }

    private void OnControlTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsProfilesTab = false;
    }

    private void OnProfilesTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsProfilesTab = true;
    }

    private void OnSettingsTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsSettingsTab = true;
    }

    private async Task<bool> ConfirmInputSourceAsync(VcpControlViewModel control)
    {
        var option = control.SelectedOption;
        if (option is null)
        {
            return false;
        }

        var dialog = new Window
        {
            Title = "切换输入源？",
            Width = 430,
            Height = 190,
            MinWidth = 430,
            MinHeight = 190,
            MaxWidth = 430,
            MaxHeight = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var message = new TextBlock
        {
            Text = $"将显示器输入切换为“{option.Label}”。画面可能暂时中断。是否继续？",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 18, 20, 12)
        };
        var cancelButton = new Button { Content = "取消", MinWidth = 88 };
        var confirmButton = new Button { Content = "切换输入源", MinWidth = 108 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(20, 8, 20, 16),
            Children = { cancelButton, confirmButton }
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Children = { message, buttons }
        };
        dialog.Content = content;
        cancelButton.Click += (_, _) => dialog.Close(false);
        confirmButton.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmDisplayModeTrialAsync(RefreshRateTrialState trial)
    {
        var countdown = new TextBlock
        {
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(20, 20, 20, 4)
        };
        var detail = new TextBlock
        {
            Text = $"显示模式已临时切换到 {ControlViewModel.DescribeResolution(trial.Trial.RequestedMode)}、{ControlViewModel.DescribeRefreshRate(trial.Trial.RequestedMode)}。\n如果画面显示正常，请在倒计时结束前保留；否则选择恢复或等待自动恢复。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 4, 20, 8)
        };
        var revertButton = new Button { Content = "立即恢复", MinWidth = 96 };
        var keepButton = new Button { Content = "保留此设置", MinWidth = 110 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(20, 8, 20, 18),
            Children = { revertButton, keepButton }
        };
        var dialog = new Window
        {
            Title = "确认显示设置",
            Width = 500,
            Height = 230,
            MinWidth = 500,
            MinHeight = 230,
            MaxWidth = 500,
            MaxHeight = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Spacing = 6,
                Children = { countdown, detail, buttons }
            }
        };
        revertButton.Click += (_, _) => dialog.Close(false);
        keepButton.Click += (_, _) => dialog.Close(true);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            var remaining = trial.Deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                countdown.Text = $"剩余 {Math.Ceiling(remaining.TotalSeconds)} 秒";
                return;
            }

            countdown.Text = "时间已到，正在恢复原显示设置…";
            keepButton.IsEnabled = false;
            revertButton.IsEnabled = false;
            var state = _viewModel?.GetRefreshRateTrialState(trial.Trial.TrialId);
            if (state?.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                countdown.Text = $"自动恢复失败：{state.LastError ?? "未知错误"}。请返回主界面重试恢复。";
                timer.Stop();
                dialog.Close(false);
            }
            else if (state?.Status == RefreshRateTrialStatus.Restored)
            {
                timer.Stop();
                dialog.Close(false);
            }
        };
        dialog.Opened += (_, _) =>
        {
            countdown.Text = $"剩余 {Math.Ceiling((trial.Deadline - DateTimeOffset.UtcNow).TotalSeconds)} 秒";
            timer.Start();
        };
        dialog.Closed += (_, _) => timer.Stop();
        return await dialog.ShowDialog<bool>(this);
    }
}
