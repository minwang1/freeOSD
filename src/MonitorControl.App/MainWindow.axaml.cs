using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MonitorControl.App.ViewModels;

namespace MonitorControl.App;

public partial class MainWindow : Window
{
    private readonly ControlViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ControlViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewModel.ConfirmInputSourceAsync = ConfirmInputSourceAsync;
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
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
        }
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
}
