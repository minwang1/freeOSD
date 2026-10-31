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
    private static readonly byte[] ContinuousCodes = [0x10, 0x12, 0x16, 0x18, 0x1A];
    private static readonly byte[] DiscreteCodes = [0x14, 0x60];

    private readonly IMonitorBackend _backend;
    private readonly IPlatformDisplaySettingsBackend? _displaySettingsBackend;
    private readonly RefreshRateTrialCoordinator? _refreshRateTrialCoordinator;
    private readonly ProfileStore _profileStore;
    private readonly ILoginStartupService? _loginStartupService;
    private readonly IDisplayColorProfileService? _displayColorProfileService;
    private readonly IForegroundApplicationMonitor? _foregroundApplicationMonitor;
    private readonly AutomationRuleStore _automationRuleStore;
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private MonitorDescriptor? _selectedMonitor;
    private bool _isLoading;
    private bool _isScanning;
    private bool _isRefreshing;
    private bool _hasScanError;
    private bool _settingSelectionInternally;
    private long _loadVersion;
    private string _statusText = "就绪。";
    private string _profileName = "";
    private string _profileStatus = "";
    private bool _isProfilesTab;
    private bool _isSettingsTab;
    private bool _isColorManagementTab;
    private DisplayColorProfile? _selectedDisplayColorProfile;
    private string _displayColorProfileStatus = "选择显示器以读取关联的 ICC 配置文件。";
    private string _displayColorProfileInspectionStatus = "选择配置文件后，可查看 ICC 文件头和校准标签摘要。";
    private string _displayColorPipelineStatus = "色彩管线能力尚未读取。";
    private int _redRgbGainPercent = 100;
    private int _greenRgbGainPercent = 100;
    private int _blueRgbGainPercent = 100;
    private int _appliedRedRgbGainPercent = 100;
    private int _appliedGreenRgbGainPercent = 100;
    private int _appliedBlueRgbGainPercent = 100;
    private bool _hasKnownAppliedRgbGains;
    private CancellationTokenSource? _rgbPreviewDebounce;
    private string? _rgbPreviewDisplayName;
    private bool _supportsVcgtAdjustment;
    private bool _calibrationLoaderEnabled;
    private string _rgbGainStatus = "当前活动 profile 的 RGB 校准状态尚未读取。";
    private bool _loginStartupEnabled;
    private string _loginStartupStatus = "";
    private MonitorProfile? _selectedProfile;
    private bool _isLoadingProfiles;
    private bool _isAutomationEnabled;
    private string _automationStatus = "";
    private ApplicationProfileRule? _selectedAutomationRule;
    private MonitorProfile? _defaultAutomationProfile;
    private readonly IInstalledApplicationCatalog? _installedApplicationCatalog;
    private bool _isLoadingAutomationSettings;
    private string? _lastAutomationKey;
    private bool _isLoadingRefreshRates;
    private bool _canSetRefreshRate;
    private bool _canSetResolution;
    private bool _hdrSupported;
    private bool _hdrEnabled;
    private string _hdrStatus = "";
    private string? _refreshRateError;
    private string _refreshRateStatusText = "";
    private string? _resolutionError;
    private string _resolutionStatusText = "";
    private DisplayMode? _selectedRefreshRateMode;
    private DisplayMode? _selectedResolutionMode;
    private IReadOnlyList<DisplayMode> _availableDisplayModes = [];
    private bool _updatingModeSelection;
    private string? _activeRefreshRateTrialId;
    private string? _restoreFailureTrialId;

    public ControlViewModel(
        IMonitorBackend backend,
        IPlatformDisplaySettingsBackend? displaySettingsBackend = null,
        RefreshRateTrialCoordinator? refreshRateTrialCoordinator = null,
        ProfileStore? profileStore = null,
        ILoginStartupService? loginStartupService = null,
        IForegroundApplicationMonitor? foregroundApplicationMonitor = null,
        AutomationRuleStore? automationRuleStore = null,
        IInstalledApplicationCatalog? installedApplicationCatalog = null,
        IDisplayColorProfileService? displayColorProfileService = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _displaySettingsBackend = displaySettingsBackend;
        _refreshRateTrialCoordinator = refreshRateTrialCoordinator;
        _profileStore = profileStore ?? new ProfileStore();
        _loginStartupService = loginStartupService;
        _displayColorProfileService = displayColorProfileService;
        _foregroundApplicationMonitor = foregroundApplicationMonitor;
        _installedApplicationCatalog = installedApplicationCatalog;
        _automationRuleStore = automationRuleStore ?? new AutomationRuleStore();
        _ = LoadLoginStartupAsync();
        if (_refreshRateTrialCoordinator is not null)
        {
            _refreshRateTrialCoordinator.StateChanged += OnRefreshRateTrialStateChanged;
        }
        RescanCommand = new AsyncCommand(RescanAsync, () => !IsScanning);
        ApplyRefreshRateCommand = new AsyncCommand(ApplyRefreshRateAsync, CanApplyRefreshRate);
        ApplyResolutionCommand = new AsyncCommand(ApplyResolutionAsync, CanApplyResolution);
        RetryRefreshRateRestoreCommand = new AsyncCommand(RetryRefreshRateRestoreAsync,
            () => _restoreFailureTrialId is not null);
        ToggleHdrCommand = new AsyncCommand(ToggleHdrAsync, () => IsHdrSupported && SelectedMonitor is not null);
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync, () => SelectedMonitor is not null && !string.IsNullOrWhiteSpace(ProfileName));
        ApplyProfileCommand = new AsyncCommand(ApplyProfileAsync, () => SelectedMonitor is not null && SelectedProfile is not null && !IsLoading);
        DeleteProfileCommand = new AsyncCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        AddAutomationRuleCommand = new AsyncCommand(AddAutomationRuleAsync, CanAddAutomationRule);
        RemoveAutomationRuleCommand = new AsyncCommand(RemoveAutomationRuleAsync, () => SelectedAutomationRule is not null);
        RefreshDisplayColorProfilesCommand = new AsyncCommand(LoadDisplayColorProfilesAsync, () => _displayColorProfileService?.IsSupported == true && SelectedMonitor is not null);
        ApplyDisplayColorProfileCommand = new AsyncCommand(ApplyDisplayColorProfileAsync, () => SelectedDisplayColorProfile is not null && SelectedMonitor is not null);
        RestoreDisplayColorProfileCommand = new AsyncCommand(RestoreDisplayColorProfileAsync, () => _hasOriginalDisplayColorProfile && SelectedMonitor is not null);
        ImportDisplayColorProfileCommand = new AsyncCommand(ImportDisplayColorProfileAsync, () => _displayColorProfileService?.IsSupported == true && SelectedMonitor is not null);
        DeleteGeneratedRgbProfileCommand = new AsyncCommand(DeleteGeneratedRgbProfileAsync, () => CanDeleteSelectedGeneratedRgbProfile);
        ApplyRgbAdjustmentCommand = new AsyncCommand(ApplyRgbAdjustmentAsync, CanApplyRgbAdjustment);
        ResetRgbAdjustmentCommand = new AsyncCommand(() => { ResetRgbAdjustment(); return Task.CompletedTask; });
        if (_foregroundApplicationMonitor is not null)
        {
            _foregroundApplicationMonitor.ForegroundApplicationChanged += OnForegroundApplicationChanged;
            _ = StartForegroundTrackingAsync();
        }
        _ = LoadProfilesAsync();
        _ = LoadAutomationRulesAsync();
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];
    public ObservableCollection<VcpControlViewModel> ContinuousControls { get; } = [];
    public ObservableCollection<VcpControlViewModel> DiscreteControls { get; } = [];
    public ICommand RescanCommand { get; }
    public ObservableCollection<DisplayMode> RefreshRateModes { get; } = [];
    public ObservableCollection<DisplayMode> ResolutionModes { get; } = [];
    public ICommand ApplyRefreshRateCommand { get; }
    public ICommand ApplyResolutionCommand { get; }
    public ICommand RetryRefreshRateRestoreCommand { get; }
    public ICommand ToggleHdrCommand { get; }
    public bool IsHdrSupported { get => _hdrSupported; private set => SetField(ref _hdrSupported, value); }
    public bool IsHdrEnabled
    {
        get => _hdrEnabled;
        private set
        {
            if (!SetField(ref _hdrEnabled, value)) return;
            if (ApplyRgbAdjustmentCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
            if (value)
            {
                CancelRgbPreviewDebounce();
                _ = RestorePendingRgbPreviewAsync();
            }
        }
    }
    public string HdrStatus { get => _hdrStatus; private set => SetField(ref _hdrStatus, value); }
    public ObservableCollection<MonitorProfile> Profiles { get; } = [];
    public bool HasProfiles => Profiles.Count > 0;
    public bool HasNoProfiles => Profiles.Count == 0;
    public ICommand SaveProfileCommand { get; }
    public ICommand ApplyProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ObservableCollection<ApplicationProfileRule> AutomationRules { get; } = [];
    public ICommand AddAutomationRuleCommand { get; }
    public ICommand RemoveAutomationRuleCommand { get; }
    public ObservableCollection<DisplayColorProfile> DisplayColorProfiles { get; } = [];
    public DisplayColorProfile? SelectedDisplayColorProfile
    {
        get => _selectedDisplayColorProfile;
        set
        {
            if (!SetField(ref _selectedDisplayColorProfile, value)) return;
            if (ApplyDisplayColorProfileCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
            if (DeleteGeneratedRgbProfileCommand is AsyncCommand delete) delete.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanDeleteSelectedGeneratedRgbProfile));
            _ = InspectSelectedDisplayColorProfileAsync(value);
        }
    }
    public string DisplayColorProfileStatus { get => _displayColorProfileStatus; private set => SetField(ref _displayColorProfileStatus, value); }
    public string DisplayColorProfileInspectionStatus { get => _displayColorProfileInspectionStatus; private set => SetField(ref _displayColorProfileInspectionStatus, value); }
    public string DisplayColorPipelineStatus { get => _displayColorPipelineStatus; private set => SetField(ref _displayColorPipelineStatus, value); }
    public int RedRgbGainPercent { get => _redRgbGainPercent; set { if (SetField(ref _redRgbGainPercent, value)) OnRgbGainChanged(); } }
    public int GreenRgbGainPercent { get => _greenRgbGainPercent; set { if (SetField(ref _greenRgbGainPercent, value)) OnRgbGainChanged(); } }
    public int BlueRgbGainPercent { get => _blueRgbGainPercent; set { if (SetField(ref _blueRgbGainPercent, value)) OnRgbGainChanged(); } }
    public string RedRgbGainLabel => $"红色 {RedRgbGainPercent}%";
    public string GreenRgbGainLabel => $"绿色 {GreenRgbGainPercent}%";
    public string BlueRgbGainLabel => $"蓝色 {BlueRgbGainPercent}%";
    public string RgbGainStatus { get => _rgbGainStatus; private set => SetField(ref _rgbGainStatus, value); }
    public bool IsRgbAdjustmentPending => !_hasKnownAppliedRgbGains || RedRgbGainPercent != _appliedRedRgbGainPercent || GreenRgbGainPercent != _appliedGreenRgbGainPercent || BlueRgbGainPercent != _appliedBlueRgbGainPercent;
    public string RgbGainPendingStatus => !_hasKnownAppliedRgbGains ? "当前活动配置的 RGB 参数未知；应用后会保存本应用的参数。" : IsRgbAdjustmentPending ? "参数有更改，尚未应用。" : "参数与当前应用的 RGB 配置一致。";
    public ICommand ApplyRgbAdjustmentCommand { get; }
    public ICommand ResetRgbAdjustmentCommand { get; }
    private bool _hasOriginalDisplayColorProfile;
    public bool IsDisplayColorProfileSupported => _displayColorProfileService?.IsSupported == true;
    public bool HasOriginalDisplayColorProfile { get => _hasOriginalDisplayColorProfile; private set { if (!SetField(ref _hasOriginalDisplayColorProfile, value)) return; if (RestoreDisplayColorProfileCommand is AsyncCommand command) command.RaiseCanExecuteChanged(); } }
    public ICommand RefreshDisplayColorProfilesCommand { get; }
    public ICommand ApplyDisplayColorProfileCommand { get; }
    public ICommand RestoreDisplayColorProfileCommand { get; }
    public ICommand ImportDisplayColorProfileCommand { get; }
    public ICommand DeleteGeneratedRgbProfileCommand { get; }
    public bool CanDeleteSelectedGeneratedRgbProfile => _displayColorProfileService?.IsSupported == true &&
        SelectedMonitor is not null && SelectedDisplayColorProfile is { IsDefault: false } profile &&
        Path.GetFileName(profile.Path).StartsWith("MonitorControl-RGB-", StringComparison.OrdinalIgnoreCase);
    public Func<string, Task<string?>>? PromptRgbProfileFileNameAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmGeneratedRgbProfileDeletionAsync { get; set; }
    public Func<Task<string?>>? PickDisplayColorProfileFileAsync { get; set; }
    public bool IsAutomationSupported => _foregroundApplicationMonitor is not null;
    public IInstalledApplicationCatalog? InstalledApplicationCatalog => _installedApplicationCatalog;
    public ValueTask<string?> GetPreviousForegroundExecutablePathAsync() =>
        _foregroundApplicationMonitor?.GetPreviousForegroundExecutablePathAsync() ?? ValueTask.FromResult<string?>(null);
    public bool IsAutomationEnabled
    {
        get => _isAutomationEnabled;
        set
        {
            if (!SetField(ref _isAutomationEnabled, value)) return;
            _ = SetAutomationEnabledAsync(value);
            if (!_isLoadingAutomationSettings) _ = SaveAutomationRulesAsync();
        }
    }
    public string AutomationStatus { get => _automationStatus; private set => SetField(ref _automationStatus, value); }
    public ApplicationProfileRule? SelectedAutomationRule { get => _selectedAutomationRule; set { if (SetField(ref _selectedAutomationRule, value) && RemoveAutomationRuleCommand is AsyncCommand command) command.RaiseCanExecuteChanged(); } }
    public MonitorProfile? DefaultAutomationProfile
    {
        get => _defaultAutomationProfile;
        set
        {
            if (!SetField(ref _defaultAutomationProfile, value)) return;
            if (!_isLoadingAutomationSettings) _ = SaveAutomationRulesAsync();
            OnPropertyChanged(nameof(DefaultAutomationProfileName));
        }
    }
    public string DefaultAutomationProfileName => DefaultAutomationProfile?.Name ?? "不应用预设";
    public string ProfileName { get => _profileName; set { if (SetField(ref _profileName, value)) RaiseProfileCommands(); } }
    public string ProfileStatus { get => _profileStatus; private set => SetField(ref _profileStatus, value); }
    public bool IsProfilesTab
    {
        get => _isProfilesTab;
        set
        {
            if (!SetField(ref _isProfilesTab, value)) return;
            if (value) { _isSettingsTab = false; _isColorManagementTab = false; }
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            OnPropertyChanged(nameof(IsColorManagementTab));
        }
    }
    public bool IsControlTab
    {
        get => !IsProfilesTab && !IsSettingsTab && !IsColorManagementTab;
        set
        {
            if (!value) return;
            _isProfilesTab = false;
            _isSettingsTab = false;
            _isColorManagementTab = false;
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsProfilesTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            OnPropertyChanged(nameof(IsColorManagementTab));
        }
    }
    public bool IsColorManagementTab
    {
        get => _isColorManagementTab;
        set
        {
            if (!SetField(ref _isColorManagementTab, value)) return;
            if (value) { _isProfilesTab = false; _isSettingsTab = false; }
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsProfilesTab));
            OnPropertyChanged(nameof(IsSettingsTab));
        }
    }
    public bool IsSettingsTab
    {
        get => _isSettingsTab;
        set
        {
            if (!SetField(ref _isSettingsTab, value)) return;
            if (value) { _isProfilesTab = false; _isColorManagementTab = false; }
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsProfilesTab));
            OnPropertyChanged(nameof(IsColorManagementTab));
        }
    }
    public bool IsLoginStartupSupported => _loginStartupService?.IsSupported == true;
    public bool LoginStartupEnabled
    {
        get => _loginStartupEnabled;
        set
        {
            if (!SetField(ref _loginStartupEnabled, value) || _loginStartupService is null) return;
            _ = UpdateLoginStartupAsync(value);
        }
    }
    public string LoginStartupStatus { get => _loginStartupStatus; private set => SetField(ref _loginStartupStatus, value); }

    public MonitorProfile? SelectedProfile { get => _selectedProfile; set { if (SetField(ref _selectedProfile, value)) { RaiseProfileCommands(); RaiseAutomationCommands(); } } }
    public bool IsLoadingProfiles { get => _isLoadingProfiles; private set => SetField(ref _isLoadingProfiles, value); }

    public bool HasRefreshRateSection => _displaySettingsBackend is not null;
    public bool HasHdrSection => _displaySettingsBackend is not null;
    public bool HasRefreshRateModes => RefreshRateModes.Count > 0;
    public bool HasNoRefreshRateModes => HasRefreshRateSection && !IsLoadingRefreshRates && !HasRefreshRateModes;
    public bool HasResolutionModes => ResolutionModes.Count > 0;
    public bool HasNoResolutionModes => HasRefreshRateSection && !IsLoadingRefreshRates && !HasResolutionModes;
    public bool IsLoadingRefreshRates
    {
        get => _isLoadingRefreshRates;
        private set
        {
            if (!SetField(ref _isLoadingRefreshRates, value)) return;
            OnPropertyChanged(nameof(HasNoRefreshRateModes));
            OnPropertyChanged(nameof(HasResolutionModes));
            OnPropertyChanged(nameof(HasNoResolutionModes));
            RaiseRefreshRateCommandState();
        }
    }
    public bool CanSetRefreshRate
    {
        get => _canSetRefreshRate;
        private set
        {
            if (!SetField(ref _canSetRefreshRate, value)) return;
            RaiseRefreshRateCommandState();
        }
    }
    public DisplayMode? SelectedRefreshRateMode
    {
        get => _selectedRefreshRateMode;
        set
        {
            if (!SetField(ref _selectedRefreshRateMode, value)) return;
            RaiseRefreshRateCommandState();
        }
    }
    public DisplayMode? SelectedResolutionMode
    {
        get => _selectedResolutionMode;
        set
        {
            if (!SetField(ref _selectedResolutionMode, value)) return;
            if (!_updatingModeSelection) PopulateRefreshRatesForResolution(value);
            RaiseRefreshRateCommandState();
        }
    }
    public bool CanSetResolution
    {
        get => _canSetResolution;
        private set
        {
            if (!SetField(ref _canSetResolution, value)) return;
            RaiseRefreshRateCommandState();
        }
    }
    public string? ResolutionError
    {
        get => _resolutionError;
        private set
        {
            if (!SetField(ref _resolutionError, value)) return;
            OnPropertyChanged(nameof(HasResolutionError));
        }
    }
    public bool HasResolutionError => !string.IsNullOrWhiteSpace(ResolutionError);
    public string ResolutionStatusText
    {
        get => _resolutionStatusText;
        private set => SetField(ref _resolutionStatusText, value);
    }
    public string? RefreshRateError
    {
        get => _refreshRateError;
        private set
        {
            if (!SetField(ref _refreshRateError, value)) return;
            OnPropertyChanged(nameof(HasRefreshRateError));
        }
    }
    public bool HasRefreshRateError => !string.IsNullOrWhiteSpace(RefreshRateError);
    public string RefreshRateStatusText
    {
        get => _refreshRateStatusText;
        private set => SetField(ref _refreshRateStatusText, value);
    }
    public bool HasRefreshRateRestoreFailure => _restoreFailureTrialId is not null;

    /// <summary>Set by the owner window to show the timed Keep/Revert prompt.</summary>
    public Func<RefreshRateTrialState, Task<bool>>? ConfirmDisplayModeTrialAsync { get; set; }

    public RefreshRateTrialState? GetRefreshRateTrialState(string trialId) =>
        _refreshRateTrialCoordinator?.GetState(trialId);

    public void Dispose()
    {
        if (_foregroundApplicationMonitor is not null)
        {
            _foregroundApplicationMonitor.ForegroundApplicationChanged -= OnForegroundApplicationChanged;
            _foregroundApplicationMonitor.Dispose();
        }
    }
    private void OnRefreshRateTrialStateChanged(object? sender, RefreshRateTrialStateEventArgs eventArgs)
    {
        if (eventArgs.State.Status is not (RefreshRateTrialStatus.RestoreFailed or RefreshRateTrialStatus.Restored)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var isResolutionTrial = eventArgs.State.Trial.ChangeKind == DisplayModeChangeKind.Resolution;
            if (eventArgs.State.Status == RefreshRateTrialStatus.RestoreFailed)
            {
                _restoreFailureTrialId = eventArgs.State.Trial.TrialId;
                var error = eventArgs.State.LastError ?? "恢复显示设置失败。";
                if (isResolutionTrial)
                {
                    ResolutionError = error;
                    ResolutionStatusText = "恢复原分辨率失败；可重试恢复。";
                }
                else
                {
                    RefreshRateError = error;
                    RefreshRateStatusText = "恢复原刷新率失败；可重试恢复。";
                }
            }
            else if (_restoreFailureTrialId == eventArgs.State.Trial.TrialId)
            {
                _restoreFailureTrialId = null;
                if (isResolutionTrial)
                {
                    ResolutionError = null;
                    ResolutionStatusText = $"已恢复原分辨率 {DescribeResolution(eventArgs.State.Trial.PreviousMode)}。";
                }
                else
                {
                    RefreshRateError = null;
                    RefreshRateStatusText = $"已恢复原刷新率 {DescribeRefreshRate(eventArgs.State.Trial.PreviousMode)}。";
                }
            }

            OnPropertyChanged(nameof(HasRefreshRateRestoreFailure));
            RaiseRefreshRateCommandState();
        });
    }

    /// <summary>Set by the owning window so input-source changes can be confirmed.</summary>
    public Func<VcpControlViewModel, Task<bool>>? ConfirmInputSourceAsync { get; set; }
    public Func<IReadOnlyList<MonitorProfile>, string?, Task<(string? ExecutablePath, string? ProfileName)?>>? AddAutomationRuleDialogAsync { get; set; }

    public MonitorDescriptor? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            var previousDisplayName = GetSelectedDisplayName(_selectedMonitor);
            if (!SetField(ref _selectedMonitor, value)) return;
            RaiseSelectionProperties();
            RaiseProfileCommands();
            if (ApplyRgbAdjustmentCommand is AsyncCommand rgbCommand) rgbCommand.RaiseCanExecuteChanged();
            var nextDisplayName = GetSelectedDisplayName(value);
            if (!string.Equals(previousDisplayName, nextDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                CancelRgbPreviewDebounce();
                _ = RestorePendingRgbPreviewAsync();
            }
            if (!_settingSelectionInternally && value is not null)
            {
                _ = LoadSelectedMonitorAsync(value);
            }
            else if (value is null)
            {
                ClearControls();
                ClearRefreshRateModes();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetField(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(HasNoMonitors));
            OnPropertyChanged(nameof(HasNoContinuousControls));
            OnPropertyChanged(nameof(ConnectionStatus));
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetField(ref _isRefreshing, value)) return;
            OnPropertyChanged(nameof(IsUiInteractionEnabled));
        }
    }

    public bool IsUiInteractionEnabled => !IsRefreshing;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetField(ref _isScanning, value)) return;
            if (RescanCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ConnectionStatus));
        }
    }

    public bool HasScanError
    {
        get => _hasScanError;
        private set
        {
            if (!SetField(ref _hasScanError, value)) return;
            OnPropertyChanged(nameof(HasNoMonitors));
        }
    }

    public bool HasMonitors => SelectedMonitor is not null;
    public bool HasNoMonitors => !IsLoading && !HasScanError && Monitors.Count == 0;
    public bool HasContinuousControls => ContinuousControls.Count > 0;
    public IReadOnlyList<VcpControlViewModel> ControlPanelContinuousControls =>
        ContinuousControls.Where(control => control.Code is not (0x16 or 0x18 or 0x1A)).ToArray();
    public bool HasNoContinuousControls => HasMonitors && !IsLoading && ControlPanelContinuousControls.Count == 0;
    public bool HasInputSource => DiscreteControls.Any(control => control.Code == 0x60);
    public string ConnectionStatus => IsLoading || IsScanning
        ? "◌ 正在读取"
        : SelectedMonitor is null ? "● 未连接" : "● 已连接";

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public Task InitializeAsync() => RescanAsync();

    private async Task RescanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        IsRefreshing = Monitors.Count > 0;
        IsLoading = true;
        HasScanError = false;
        StatusText = "正在查找显示器…";
        var loadVersion = Interlocked.Increment(ref _loadVersion);

        try
        {
            await _deviceGate.WaitAsync();
            IReadOnlyList<MonitorDescriptor> discovered;
            try
            {
                discovered = await _backend.DiscoverMonitorsAsync();
            }
            finally
            {
                _deviceGate.Release();
            }

            var previousId = SelectedMonitor?.Id;
            // Keep the last usable monitor list visible until discovery succeeds.
            var discoveredIds = discovered.Select(monitor => monitor.Id).ToHashSet();
            for (var index = Monitors.Count - 1; index >= 0; index--)
                if (!discoveredIds.Contains(Monitors[index].Id)) Monitors.RemoveAt(index);
            foreach (var monitor in discovered)
                if (!Monitors.Any(existing => existing.Id == monitor.Id)) Monitors.Add(monitor);
            OnPropertyChanged(nameof(HasNoMonitors));

            // Reuse the selected descriptor instance when it remains present. Clearing and
            // rebuilding this collection briefly resets ComboBox.SelectedItem to null.
            var selected = Monitors.FirstOrDefault(monitor => monitor.Id == previousId)
                ?? Monitors.FirstOrDefault();
            _settingSelectionInternally = true;
            try
            {
                SelectedMonitor = selected;
            }
            finally
            {
                _settingSelectionInternally = false;
            }

            if (selected is null)
            {
                ClearControls();
                StatusText = "未发现可访问的显示器。请检查 DDC/CI 设置后重新扫描。";
            }
            else
            {
                await LoadMonitorDataAsync(selected, loadVersion);
                await LoadRefreshRateModesAsync(selected, loadVersion);
                await LoadDisplayColorProfilesAsync();
            }
        }
        catch (Exception exception)
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                HasScanError = true;
                // Preserve existing monitor data and controls when a background refresh fails.
                if (SelectedMonitor is null) ClearControls();
                StatusText = SelectedMonitor is null
                    ? $"扫描显示器失败：{exception.Message}"
                    : $"刷新失败，仍显示上次读取的数据：{exception.Message}";
            }
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
            }
            IsScanning = false;
            IsRefreshing = false;
        }
    }

    private async Task LoadSelectedMonitorAsync(MonitorDescriptor monitor)
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        IsLoading = true;
        StatusText = $"正在读取 {monitor.Name}…";
        try
        {
            await LoadMonitorDataAsync(monitor, loadVersion);
            await LoadRefreshRateModesAsync(monitor, loadVersion);
            await LoadDisplayColorProfilesAsync();
        }
        catch (Exception exception)
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                StatusText = $"刷新 {monitor.Name} 失败，仍显示上次读取的数据：{exception.Message}";
            }
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadMonitorDataAsync(MonitorDescriptor monitor, long loadVersion)
    {
        await _deviceGate.WaitAsync();
        MonitorCapabilities capabilities;
        var continuous = new List<VcpControlViewModel>();
        var discrete = new List<VcpControlViewModel>();
        try
        {
            capabilities = await _backend.GetCapabilitiesAsync(monitor.Id);
            var features = capabilities.VcpFeatures
                .Where(feature => feature.Availability != CapabilityAvailability.Unsupported)
                .Where(feature => feature.Discovery == VcpCapabilityDiscovery.Advertised)
                .GroupBy(feature => feature.Code)
                .ToDictionary(group => group.Key, group => group.First());
            DiagnosticLog.Write("INFO", "control.capabilities.loaded", $"featureCount={features.Count} continuousCandidates={ContinuousCodes.Length} discreteCandidates={DiscreteCodes.Length}");

            foreach (var code in ContinuousCodes)
            {
                if (!features.TryGetValue(code, out var feature)) continue;
                var (name, _, _) = Describe(code);
                try
                {
                    var value = await _backend.ReadVcpAsync(monitor.Id, code);
                    DiagnosticLog.Write("INFO", "control.vcp.readable", $"code=0x{code:X2} current={value.Current} maximum={value.Maximum}");
                    var maximum = value.Maximum is > 0 ? value.Maximum.Value
                        : feature.MaximumValue is > 0 ? feature.MaximumValue.Value : 100u;
                    continuous.Add(new VcpControlViewModel(this, code, name, true,
                        monitor.Id, maximum, null, value.Current));
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Write("WARN", "control.vcp.unreadable", $"code=0x{code:X2} errorType={exception.GetType().Name} message={exception.Message}");
                    continuous.Add(new VcpControlViewModel(this, code, name, true,
                        monitor.Id, feature.MaximumValue is > 0 ? feature.MaximumValue.Value : 100u,
                        exception.Message, null));
                }
            }

            foreach (var code in DiscreteCodes)
            {
                if (!features.TryGetValue(code, out var feature)) continue;
                var (name, _, fallbackOptions) = Describe(code);
                var friendlyLabels = fallbackOptions.ToDictionary(option => option.Value, option => option.Label);
                var options = feature.EnumeratedValues is { Count: > 0 }
                    ? feature.EnumeratedValues.Select(pair => new VcpValueOption(pair.Key,
                        friendlyLabels.TryGetValue(pair.Key, out var friendlyLabel)
                            ? friendlyLabel
                            : string.IsNullOrWhiteSpace(pair.Value) ? $"0x{pair.Key:X2}" : pair.Value)).ToArray()
                    : fallbackOptions;
                try
                {
                    var value = await _backend.ReadVcpAsync(monitor.Id, code);
                    DiagnosticLog.Write("INFO", "control.vcp.readable", $"code=0x{code:X2} current={value.Current} maximum={value.Maximum}");
                    discrete.Add(new VcpControlViewModel(this, code, name, false,
                        monitor.Id, 0, null, value.Current, options));
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Write("WARN", "control.vcp.unreadable", $"code=0x{code:X2} errorType={exception.GetType().Name} message={exception.Message}");
                    discrete.Add(new VcpControlViewModel(this, code, name, false,
                        monitor.Id, 0, exception.Message, null, options));
                }
            }
        }
        finally
        {
            _deviceGate.Release();
        }

        if (loadVersion != Volatile.Read(ref _loadVersion) || SelectedMonitor?.Id != monitor.Id)
        {
            return;
        }

        ClearControls();
        foreach (var control in continuous) ContinuousControls.Add(control);
        foreach (var control in discrete) DiscreteControls.Add(control);
        NotifyControlSummaryChanged();
        StatusText = $"已读取 {monitor.Name} 的显示器能力。";
    }

    private async Task LoadRefreshRateModesAsync(MonitorDescriptor monitor, long loadVersion)
    {
        ClearRefreshRateModes();
        if (_displaySettingsBackend is null)
        {
            OnPropertyChanged(nameof(HasRefreshRateSection));
            return;
        }

        IsLoadingRefreshRates = true;
        RefreshRateError = null;
        RefreshRateStatusText = "正在读取可用刷新率…";
        try
        {
            var capabilities = await _displaySettingsBackend.GetCapabilitiesAsync(monitor.Id);
            if (loadVersion != Volatile.Read(ref _loadVersion) || SelectedMonitor?.Id != monitor.Id) return;

            CanSetRefreshRate = capabilities.SetRefreshRate == CapabilityAvailability.Supported;
            CanSetResolution = capabilities.SetResolution == CapabilityAvailability.Supported;
            IsHdrSupported = capabilities.ReadHdr == CapabilityAvailability.Supported &&
                             capabilities.SetHdr == CapabilityAvailability.Supported;
            if (IsHdrSupported)
            {
                var hdrState = await _displaySettingsBackend.GetHdrStateAsync(monitor.Id);
                IsHdrSupported = hdrState.IsSupported;
                IsHdrEnabled = hdrState.IsEnabled;
                HdrStatus = IsHdrSupported ? (IsHdrEnabled ? "HDR 已开启。" : "HDR 已关闭。") : "当前显示器不支持 HDR。";
            }
            else HdrStatus = "当前显示器或连接路径不支持 HDR 控制。";
            if (ToggleHdrCommand is AsyncCommand hdrCommand) hdrCommand.RaiseCanExecuteChanged();
            if (capabilities.EnumerateDisplayModes != CapabilityAvailability.Supported)
            {
                RefreshRateStatusText = capabilities.EnumerateDisplayModes == CapabilityAvailability.Unsupported
                    ? "当前显示器或平台不支持查询显示模式。"
                    : "当前无法查询显示模式。";
                ResolutionStatusText = RefreshRateStatusText;
                return;
            }

            var modes = await _displaySettingsBackend.GetAvailableModesAsync(monitor.Id);
            if (loadVersion != Volatile.Read(ref _loadVersion) || SelectedMonitor?.Id != monitor.Id) return;

            var currentMode = modes.FirstOrDefault(mode => mode.IsCurrent);
            var compatibleModes = currentMode is null
                ? modes
                : modes.Where(mode => mode.BitsPerPixel == currentMode.BitsPerPixel &&
                                      mode.ScanLineOrdering == currentMode.ScanLineOrdering &&
                                      mode.Orientation == currentMode.Orientation);
            var uniqueModes = compatibleModes
                .GroupBy(mode => (
                    mode.Width,
                    mode.Height,
                    mode.RefreshRateNumerator,
                    mode.RefreshRateDenominator,
                    mode.BitsPerPixel))
                .Select(group => group
                    .OrderByDescending(mode => mode.IsCurrent)
                    .ThenBy(mode => mode.ScanLineOrdering == 0 ? 0 : 1)
                    .First())
                .OrderByDescending(mode => mode.IsCurrent)
                .ThenBy(mode => mode.RefreshRateNumerator / (double)Math.Max(1, mode.RefreshRateDenominator))
                .ThenBy(mode => mode.Width)
                .ThenBy(mode => mode.Height);

            var allModes = uniqueModes.ToArray();
            _availableDisplayModes = allModes;
            foreach (var mode in allModes
                         .GroupBy(mode => (mode.Width, mode.Height, mode.BitsPerPixel))
                         .Select(group => group.OrderByDescending(mode => mode.IsCurrent).First())
                         .OrderByDescending(mode => mode.IsCurrent)
                         .ThenByDescending(mode => mode.Width)
                         .ThenByDescending(mode => mode.Height))
            {
                ResolutionModes.Add(mode);
            }

            OnPropertyChanged(nameof(HasRefreshRateModes));
            OnPropertyChanged(nameof(HasNoRefreshRateModes));
            OnPropertyChanged(nameof(HasResolutionModes));
            OnPropertyChanged(nameof(HasNoResolutionModes));
            _updatingModeSelection = true;
            SelectedResolutionMode = ResolutionModes.FirstOrDefault(mode => mode.IsCurrent)
                ?? ResolutionModes.FirstOrDefault();
            _updatingModeSelection = false;
            PopulateRefreshRatesForResolution(SelectedResolutionMode);
            RefreshRateStatusText = CanSetRefreshRate
                ? "选择分辨率后显示该分辨率支持的刷新率。选择后可先试用 15 秒。"
                : "当前平台可读取刷新率，但不支持修改。";
            ResolutionStatusText = CanSetResolution
                ? "系统支持的分辨率按从高到低排列。"
                : "当前平台不支持修改分辨率。";
        }
        catch (Exception exception)
        {
            if (loadVersion == Volatile.Read(ref _loadVersion) && SelectedMonitor?.Id == monitor.Id)
            {
                CanSetRefreshRate = false;
                CanSetResolution = false;
                RefreshRateError = exception.Message;
                ResolutionError = exception.Message;
                RefreshRateStatusText = "读取刷新率失败。";
                ResolutionStatusText = "读取分辨率失败。";
            }
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion) && SelectedMonitor?.Id == monitor.Id)
            {
                IsLoadingRefreshRates = false;
            }
        }
    }

}
