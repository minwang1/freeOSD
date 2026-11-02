using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MonitorControl.Core.Services;
using MonitorControl.Core.Models;
using MonitorControl.App.ViewModels;
using MonitorControl.App.Services;
using ArcButton = TKCode.Arc.Button;
using TKCode.Arc.DesignTokens;

namespace MonitorControl.App;

public partial class MainWindow : Window
{
    private static string T(string source) => LocalizationService.Translate(source);

    private readonly ControlViewModel? _viewModel;
    private readonly WindowSettingsStore _windowSettingsStore = new();
    private static readonly AppearanceOption[] AppearanceOptions =
    [
        new(AppearanceMode.System, LocalizationService.Translate("Appearance.System")),
        new(AppearanceMode.Light, LocalizationService.Translate("Appearance.Light")),
        new(AppearanceMode.Dark, LocalizationService.Translate("Appearance.Dark"))
    ];

    private static readonly AccentColorOption[] AccentOptions =
    [
        new(AccentColor.SoftBlue, LocalizationService.Translate("Accent.Blue"), new SolidColorBrush(Color.FromRgb(105, 151, 190))),
        new(AccentColor.SoftGreen, LocalizationService.Translate("Accent.Green"), new SolidColorBrush(Color.FromRgb(105, 157, 130))),
        new(AccentColor.SoftOrange, LocalizationService.Translate("Accent.Orange"), new SolidColorBrush(Color.FromRgb(204, 139, 94))),
        new(AccentColor.Charcoal, LocalizationService.Translate("Accent.Charcoal"), new SolidColorBrush(Color.FromRgb(80, 88, 96)))
    ];

    private static readonly LanguageOption[] LanguageOptions =
    [
        new(UiLanguage.Chinese, "中文"),
        new(UiLanguage.English, "English")
    ];

    private sealed record AppearanceOption(AppearanceMode Mode, string Label);
    private sealed record LanguageOption(UiLanguage Language, string Label);

    public bool AllowClose { get; set; }
    public bool TrayAvailable { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        ConfigureArcPickerTextSelectors();
    }

    public MainWindow(
        ControlViewModel viewModel,
        AppearanceMode appearanceMode = AppearanceMode.System,
        AccentColor accentColor = AccentColor.SoftBlue,
        UiLanguage language = UiLanguage.Chinese)
    {
        InitializeComponent();
        ConfigureArcPickerTextSelectors();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewModel.ConfirmInputSourceAsync = ConfirmInputSourceAsync;
        _viewModel.ConfirmDisplayModeTrialAsync = ConfirmDisplayModeTrialAsync;
        _viewModel.AddAutomationRuleDialogAsync = AddAutomationRuleDialogAsync;
        _viewModel.PickDisplayColorProfileFileAsync = PickDisplayColorProfileFileAsync;
        _viewModel.PromptRgbProfileFileNameAsync = PromptRgbProfileFileNameAsync;
        _viewModel.ConfirmGeneratedRgbProfileDeletionAsync = ConfirmGeneratedRgbProfileDeletionAsync;
        DataContext = _viewModel;
        ConfigureAppearancePickers(appearanceMode, accentColor, language);
        if (this.FindControl<TextBlock>("VersionText") is { } versionText)
            versionText.Text = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        Opened += RestoreWindowSettingsAsync;
        Opened += OnOpened;
        Closing += SaveWindowSettings;
        Closing += CloseToTray;
        Closed += OnClosed;
    }

    private void ConfigureAppearancePickers(AppearanceMode appearanceMode, AccentColor accentColor, UiLanguage language)
    {
        if (this.FindControl<TKCode.Arc.Combobox>("AppearancePicker") is { } appearancePicker)
        {
            appearancePicker.ItemsSource = AppearanceOptions;
            appearancePicker.TextSelector = static item => (item as AppearanceOption)?.Label;
            appearancePicker.SelectedItem = AppearanceOptions.First(option => option.Mode == appearanceMode);
            appearancePicker.SelectionChanged += (_, _) =>
            {
                if (appearancePicker.SelectedItem is AppearanceOption option)
                    App.SetAppearanceMode(option.Mode);
            };
        }

        if (this.FindControl<TKCode.Arc.Combobox>("AccentPicker") is { } accentPicker)
        {
            accentPicker.ItemsSource = AccentOptions;
            accentPicker.TextSelector = static item => (item as AccentColorOption)?.Label;
            accentPicker.SelectedItem = AccentOptions.First(option => option.Color == accentColor);
            accentPicker.SelectionChanged += (_, _) =>
            {
                if (accentPicker.SelectedItem is AccentColorOption option)
                    App.SetAccentColor(option.Color);
            };
        }

        if (this.FindControl<TKCode.Arc.Combobox>("LanguagePicker") is { } languagePicker)
        {
            languagePicker.ItemsSource = LanguageOptions;
            languagePicker.TextSelector = static item => (item as LanguageOption)?.Label;
            languagePicker.SelectedItem = LanguageOptions.First(option => option.Language == language);
            languagePicker.SelectionChanged += (_, _) =>
            {
                if (languagePicker.SelectedItem is LanguageOption option) App.SetLanguage(option.Language);
            };
        }
    }

    public void UpdateLocalizedOptions(UiLanguage language)
    {
        _viewModel?.NotifyLocalizationChanged();
        if (_viewModel is not null)
        {
            foreach (var control in _viewModel.ContinuousControls.Concat(_viewModel.DiscreteControls))
            {
                control.NotifyLocalizationChanged();
                foreach (var option in control.Options) option.NotifyLocalizationChanged();
            }
        }
        if (this.FindControl<TKCode.Arc.Combobox>("InputSourcePicker") is { SelectedItem: VcpValueOption selectedInput } inputPicker)
        {
            inputPicker.SelectedItem = null;
            inputPicker.SelectedItem = selectedInput;
        }
        static void ReplaceItems<T>(TKCode.Arc.Combobox picker, T[] items, Func<T, string> label, T selected)
        {
            picker.ItemsSource = items;
            picker.TextSelector = item => item is T value ? label(value) : null;
            picker.SelectedItem = selected;
        }
        if (this.FindControl<TKCode.Arc.Combobox>("AppearancePicker") is { SelectedItem: AppearanceOption selectedAppearance } appearancePicker)
        {
            var items = Enum.GetValues<AppearanceMode>().Select(mode => new AppearanceOption(mode, LocalizationService.Translate($"Appearance.{mode}"))).ToArray();
            ReplaceItems(appearancePicker, items, option => option.Label, items.First(option => option.Mode == selectedAppearance.Mode));
        }
        if (this.FindControl<TKCode.Arc.Combobox>("AccentPicker") is { SelectedItem: AccentColorOption selectedAccent } accentPicker)
        {
            var items = AccentOptions.Select(option => option with { Label = LocalizationService.Translate($"Accent.{option.Color switch { AccentColor.SoftBlue => "Blue", AccentColor.SoftGreen => "Green", AccentColor.SoftOrange => "Orange", _ => "Charcoal" }}") }).ToArray();
            ReplaceItems(accentPicker, items, option => option.Label, items.First(option => option.Color == selectedAccent.Color));
        }
        if (this.FindControl<TKCode.Arc.Combobox>("LanguagePicker") is { } languagePicker)
        {
            var items = new[] { new LanguageOption(UiLanguage.Chinese, "中文"), new LanguageOption(UiLanguage.English, "English") };
            languagePicker.ItemsSource = items;
            languagePicker.TextSelector = static item => (item as LanguageOption)?.Label;
            languagePicker.SelectedItem = items.First(option => option.Language == language);
        }
    }

    private void ConfigureArcPickerTextSelectors()
    {
        if (this.FindControl<TKCode.Arc.Combobox>("MonitorPicker") is { } monitorPicker)
            monitorPicker.TextSelector = static item => (item as MonitorDescriptor)?.Name;
        if (this.FindControl<TKCode.Arc.Combobox>("InputSourcePicker") is { } inputPicker)
            inputPicker.TextSelector = static item => (item as VcpValueOption)?.LocalizedLabel;
        if (this.FindControl<TKCode.Arc.Combobox>("ResolutionPicker") is { } resolutionPicker)
            resolutionPicker.TextSelector = static item => item is DisplayMode mode ? $"{mode.Width} × {mode.Height}" : null;
        if (this.FindControl<TKCode.Arc.Combobox>("RefreshRatePicker") is { } refreshPicker)
            refreshPicker.TextSelector = static item => item is DisplayMode mode ? $"{mode.RefreshRateHz:0.##} Hz" : null;
        if (this.FindControl<TKCode.Arc.Combobox>("ProfilePicker") is { } profilePicker)
            profilePicker.TextSelector = static item => (item as MonitorProfile)?.Name;
        if (this.FindControl<TKCode.Arc.Combobox>("AutomationRulePicker") is { } rulePicker)
            rulePicker.TextSelector = static item => item is ApplicationProfileRule rule
                ? $"{Path.GetFileNameWithoutExtension(rule.ExecutablePath)} → {rule.ProfileName}"
                : null;
        if (this.FindControl<TKCode.Arc.Combobox>("FallbackProfilePicker") is { } fallbackPicker)
            fallbackPicker.TextSelector = static item => (item as MonitorProfile)?.Name;
        if (this.FindControl<TKCode.Arc.Combobox>("ColorProfilePicker") is { } colorProfilePicker)
            colorProfilePicker.TextSelector = static item => (item as DisplayColorProfile)?.Name;

        if (this.FindControl<TKCode.Arc.Slider>("RedRgbSlider") is { } redSlider)
            redSlider.ValueFormatter = static value => $"{value:0}%";
        if (this.FindControl<TKCode.Arc.Slider>("GreenRgbSlider") is { } greenSlider)
            greenSlider.ValueFormatter = static value => $"{value:0}%";
        if (this.FindControl<TKCode.Arc.Slider>("BlueRgbSlider") is { } blueSlider)
            blueSlider.ValueFormatter = static value => $"{value:0}%";
    }

    private async Task<string?> PickDisplayColorProfileFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择显示器 ICC/ICM 配置文件"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("ICC/ICM 配置文件")) { Patterns = ["*.icc", "*.icm"] }
            ]
        });
        var selectedFile = files.FirstOrDefault();
        if (selectedFile is null) return null;
        var localPath = selectedFile.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
            throw new InvalidOperationException("The selected ICC/ICM file is not available as a local Windows file.");
        return localPath;
    }

    private async Task<string?> PromptRgbProfileFileNameAsync(string defaultFileName)
    {
        var message = new TextBlock
        {
            Text = T("为当前 RGB 调整设置 profile 文件名。应用后会安装并设为当前显示器的默认 ICC profile。"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 18, 20, 8)
        };
        var fileNameBox = new TKCode.Arc.Input
        {
            Label = T("Profile 文件名"),
            Text = defaultFileName,
            Margin = new Avalonia.Thickness(20, 0, 20, 8)
        };
        var cancelButton = new ArcButton { Content = T("取消"), MinWidth = 88, Variant = TKCode.Arc.ButtonVariant.Secondary, Size = TKCode.Arc.ControlSize.Small };
        var saveButton = new ArcButton { Content = T("确认并应用"), MinWidth = 110, Variant = TKCode.Arc.ButtonVariant.Primary, Size = TKCode.Arc.ControlSize.Small };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(20, 8, 20, 16),
            Children = { cancelButton, saveButton }
        };
        var dialog = new Window
        {
            Title = T("保存 RGB profile"),
            Width = 520,
            Height = 230,
            MinWidth = 520,
            MinHeight = 230,
            MaxWidth = 520,
            MaxHeight = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel { Spacing = 8, Children = { message, fileNameBox, buttons } }
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        saveButton.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => { fileNameBox.Focus(); fileNameBox.SelectAll(); };
        var accepted = await dialog.ShowDialog<bool>(this);
        return accepted ? fileNameBox.Text?.Trim() : null;
    }

    private async Task<bool> ConfirmGeneratedRgbProfileDeletionAsync(string profileName)
    {
        var message = new TextBlock
        {
            Text = T($"确定删除 RGB profile“{profileName}”吗？"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 18, 20, 12)
        };
        var cancelButton = new ArcButton { Content = T("取消"), MinWidth = 88, Variant = TKCode.Arc.ButtonVariant.Secondary, Size = TKCode.Arc.ControlSize.Small };
        var deleteButton = new ArcButton { Content = T("删除 profile"), MinWidth = 110, Variant = TKCode.Arc.ButtonVariant.Danger, Size = TKCode.Arc.ControlSize.Small };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(20, 8, 20, 16),
            Children = { cancelButton, deleteButton }
        };
        var dialog = new Window
        {
            Title = T("确认删除"),
            Width = 520,
            Height = 220,
            MinWidth = 520,
            MinHeight = 220,
            MaxWidth = 520,
            MaxHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel { Spacing = 8, Children = { message, buttons } }
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        deleteButton.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
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
            _viewModel.AddAutomationRuleDialogAsync = null;
            _viewModel.PickDisplayColorProfileFileAsync = null;
            _viewModel.PromptRgbProfileFileNameAsync = null;
            _viewModel.ConfirmGeneratedRgbProfileDeletionAsync = null;
            _viewModel.Dispose();
        }
    }

    private void OnControlTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsControlTab = true;
    }

    private void OnColorManagementTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsColorManagementTab = true;
    }

    private void OnProfilesTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsProfilesTab = true;
    }

    private void OnSettingsTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null) _viewModel.IsSettingsTab = true;
    }

    private async Task<(string? ExecutablePath, string? ProfileName)?> AddAutomationRuleDialogAsync(
        IReadOnlyList<MonitorProfile> profiles, string? defaultProfileName)
    {
        if (_viewModel?.InstalledApplicationCatalog is not { } catalog) return null;
        var apps = await catalog.GetApplicationsAsync();
        var profilePicker = new TKCode.Arc.Combobox
        {
            Label = T("应用预设"),
            ItemsSource = profiles,
            SelectedItem = profiles.FirstOrDefault(profile => string.Equals(profile.Name, defaultProfileName, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault(),
            TextSelector = static item => (item as MonitorProfile)?.Name,
            Margin = new Avalonia.Thickness(12, 4, 12, 8)
        };
        var title = new TextBlock
        {
            Text = T($"选择要应用预设“{(profilePicker.SelectedItem as MonitorProfile)?.Name}”的已安装程序。"),
            Margin = new Avalonia.Thickness(12, 10, 12, 4)
        };
        profilePicker.ItemTemplate = new FuncDataTemplate<MonitorProfile>((profile, _) => new TextBlock { Text = profile?.Name ?? "" });
        var search = new TKCode.Arc.Input
        {
            Label = T("搜索已安装程序"),
            Placeholder = T("搜索已安装程序"),
            Margin = new Avalonia.Thickness(12, 4, 6, 8)
        };
        var previousApplicationButton = new ArcButton
        {
            Content = T("使用上一个前台程序"),
            Variant = TKCode.Arc.ButtonVariant.Secondary,
            Size = TKCode.Arc.ControlSize.Small,
            Margin = new Avalonia.Thickness(6, 4, 12, 8)
        };
        var selectedApplicationText = new TextBlock
        {
            Text = T("未选择程序"),
            Margin = new Avalonia.Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap
        };
        TKCode.Arc.ArcTokens.Bind(selectedApplicationText, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        string? selectedExecutablePath = null;
        var applicationList = new ListBox
        {
            ItemsSource = apps,
            Margin = new Avalonia.Thickness(12, 0, 12, 8),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<InstalledApplication>((app, _) =>
            {
                var executableName = new TextBlock
                {
                    Text = app is null ? "" : Path.GetFileName(app.ExecutablePath)
                };
                TKCode.Arc.ArcTokens.Bind(executableName, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = app?.DisplayName ?? "", Width = 330, TextTrimming = TextTrimming.CharacterEllipsis },
                        executableName
                    }
                };
            })
        };
        var okButton = new ArcButton { Content = T("添加规则"), MinWidth = 90, IsEnabled = false, Variant = TKCode.Arc.ButtonVariant.Primary, Size = TKCode.Arc.ControlSize.Small };
        var cancelButton = new ArcButton { Content = T("取消"), MinWidth = 90, Variant = TKCode.Arc.ButtonVariant.Secondary, Size = TKCode.Arc.ControlSize.Small };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        searchRow.Children.Add(search);
        searchRow.Children.Add(previousApplicationButton);
        Grid.SetColumn(previousApplicationButton, 1);
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(12, 4, 12, 12),
            Children = { cancelButton, okButton }
        };
        var contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
                new RowDefinition(GridLength.Auto)
            }
        };
        contentGrid.Children.Add(title);
        contentGrid.Children.Add(profilePicker);
        contentGrid.Children.Add(searchRow);
        contentGrid.Children.Add(selectedApplicationText);
        contentGrid.Children.Add(applicationList);
        contentGrid.Children.Add(buttonRow);
        Grid.SetRow(profilePicker, 1);
        Grid.SetRow(searchRow, 2);
        Grid.SetRow(selectedApplicationText, 3);
        Grid.SetRow(applicationList, 4);
        Grid.SetRow(buttonRow, 5);
        var dialog = new Window
        {
            Title = T("添加应用规则"),
            Width = 820,
            Height = 620,
            MinWidth = 600,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = contentGrid
        };
        void UpdateAddButton() => okButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedExecutablePath) && profilePicker.SelectedItem is MonitorProfile;
        applicationList.SelectionChanged += (_, _) =>
        {
            selectedExecutablePath = (applicationList.SelectedItem as InstalledApplication)?.ExecutablePath;
            selectedApplicationText.Text = selectedExecutablePath is null
                ? T("未选择程序")
                : $"{Path.GetFileName(selectedExecutablePath)}\n{selectedExecutablePath}";
            UpdateAddButton();
        };
        profilePicker.SelectionChanged += (_, _) =>
        {
            UpdateAddButton();
            title.Text = T($"选择要应用预设“{(profilePicker.SelectedItem as MonitorProfile)?.Name}”的已安装程序。");
        };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? "";
            applicationList.ItemsSource = string.IsNullOrEmpty(query)
                ? apps
                : apps.Where(app => app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || app.ExecutablePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            applicationList.SelectedItem = null;
            selectedExecutablePath = null;
            selectedApplicationText.Text = T("未选择程序");
            UpdateAddButton();
        };
        previousApplicationButton.Click += async (_, _) =>
        {
            var previousPath = await _viewModel!.GetPreviousForegroundExecutablePathAsync();
            if (string.IsNullOrWhiteSpace(previousPath))
            {
                selectedExecutablePath = null;
                selectedApplicationText.Text = T("还没有记录到上一个前台程序。先切换到目标程序，再返回此窗口添加规则。");
                UpdateAddButton();
                return;
            }
            selectedExecutablePath = previousPath;
            applicationList.SelectedItem = apps.FirstOrDefault(app => string.Equals(app.ExecutablePath, previousPath, StringComparison.OrdinalIgnoreCase));
                selectedApplicationText.Text = $"{Path.GetFileName(previousPath)}\n{previousPath}";
            UpdateAddButton();
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        okButton.Click += (_, _) => dialog.Close(true);
        if (apps.Count == 0)
        {
            title.Text = T("没有找到可执行文件路径有效的已安装程序。也可以使用“上一个前台程序”。");
            applicationList.IsEnabled = false;
        }
        var accepted = await dialog.ShowDialog<bool>(this);
        return accepted && !string.IsNullOrWhiteSpace(selectedExecutablePath) && profilePicker.SelectedItem is MonitorProfile selectedProfile
            ? (selectedExecutablePath, selectedProfile.Name)
            : null;
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
            Title = T("切换输入源？"),
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
            Text = T($"将显示器输入切换为“{option.LocalizedLabel}”。画面可能暂时中断。是否继续？"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 18, 20, 12)
        };
        var cancelButton = new ArcButton { Content = T("取消"), MinWidth = 88, Variant = TKCode.Arc.ButtonVariant.Secondary, Size = TKCode.Arc.ControlSize.Small };
        var confirmButton = new ArcButton { Content = T("切换输入源"), MinWidth = 108, Variant = TKCode.Arc.ButtonVariant.Primary, Size = TKCode.Arc.ControlSize.Small };
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
            Text = T($"显示模式已临时切换到 {ControlViewModel.DescribeResolution(trial.Trial.RequestedMode)}、{ControlViewModel.DescribeRefreshRate(trial.Trial.RequestedMode)}。\n如果画面显示正常，请在倒计时结束前保留；否则选择恢复或等待自动恢复。"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 4, 20, 8)
        };
        var revertButton = new ArcButton { Content = T("立即恢复"), MinWidth = 96, Variant = TKCode.Arc.ButtonVariant.Secondary, Size = TKCode.Arc.ControlSize.Small };
        var keepButton = new ArcButton { Content = T("保留此设置"), MinWidth = 110, Variant = TKCode.Arc.ButtonVariant.Primary, Size = TKCode.Arc.ControlSize.Small };
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
            Title = T("确认显示设置"),
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
                countdown.Text = T($"剩余 {Math.Ceiling(remaining.TotalSeconds)} 秒");
                return;
            }

            countdown.Text = T("时间已到，正在恢复原显示设置…");
            keepButton.IsEnabled = false;
            revertButton.IsEnabled = false;
            var state = _viewModel?.GetRefreshRateTrialState(trial.Trial.TrialId);
            if (state?.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                countdown.Text = T($"自动恢复失败：{state.LastError ?? T("未知错误")}。请返回主界面重试恢复。" );
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
            countdown.Text = T($"剩余 {Math.Ceiling((trial.Deadline - DateTimeOffset.UtcNow).TotalSeconds)} 秒");
            timer.Start();
        };
        dialog.Closed += (_, _) => timer.Stop();
        return await dialog.ShowDialog<bool>(this);
    }
}
