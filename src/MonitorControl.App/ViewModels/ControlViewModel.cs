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

public sealed class ControlViewModel : ObservableObject
{
    private static readonly byte[] ContinuousCodes = [0x10, 0x12, 0x16, 0x18, 0x1A];
    private static readonly byte[] DiscreteCodes = [0x14, 0x60];

    private readonly IMonitorBackend _backend;
    private readonly IPlatformDisplaySettingsBackend? _displaySettingsBackend;
    private readonly RefreshRateTrialCoordinator? _refreshRateTrialCoordinator;
    private readonly ProfileStore _profileStore;
    private readonly ILoginStartupService? _loginStartupService;
    private readonly IForegroundApplicationMonitor? _foregroundApplicationMonitor;
    private readonly AutomationRuleStore _automationRuleStore;
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private MonitorDescriptor? _selectedMonitor;
    private bool _isLoading;
    private bool _isScanning;
    private bool _hasScanError;
    private bool _settingSelectionInternally;
    private long _loadVersion;
    private string _statusText = "就绪。";
    private string _profileName = "";
    private string _profileStatus = "";
    private bool _isProfilesTab;
    private bool _isSettingsTab;
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
        IInstalledApplicationCatalog? installedApplicationCatalog = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _displaySettingsBackend = displaySettingsBackend;
        _refreshRateTrialCoordinator = refreshRateTrialCoordinator;
        _profileStore = profileStore ?? new ProfileStore();
        _loginStartupService = loginStartupService;
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
    public bool IsHdrEnabled { get => _hdrEnabled; private set => SetField(ref _hdrEnabled, value); }
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
            if (value) _isSettingsTab = false;
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsSettingsTab));
        }
    }
    public bool IsControlTab => !IsProfilesTab && !IsSettingsTab;
    public bool IsSettingsTab
    {
        get => _isSettingsTab;
        set
        {
            if (!SetField(ref _isSettingsTab, value)) return;
            if (value) _isProfilesTab = false;
            OnPropertyChanged(nameof(IsControlTab));
            OnPropertyChanged(nameof(IsProfilesTab));
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
            if (!SetField(ref _selectedMonitor, value)) return;
            RaiseSelectionProperties();
            RaiseProfileCommands();
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
    public bool HasNoContinuousControls => HasMonitors && !IsLoading && !HasContinuousControls;
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
            Monitors.Clear();
            foreach (var monitor in discovered)
            {
                Monitors.Add(monitor);
            }
            OnPropertyChanged(nameof(HasNoMonitors));

            var selected = discovered.FirstOrDefault(monitor => monitor.Id == previousId)
                ?? discovered.FirstOrDefault();
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
            }
        }
        catch (Exception exception)
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                HasScanError = true;
                ClearControls();
                StatusText = $"扫描显示器失败：{exception.Message}";
            }
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
            }
            IsScanning = false;
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
        }
        catch (Exception exception)
        {
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                ClearControls();
                StatusText = $"读取 {monitor.Name} 失败：{exception.Message}";
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
            _lastAutomationKey = key;
            AutomationStatus = $"检测到 {Path.GetFileName(eventArgs.ExecutablePath)}，正在应用“{profile.Name}”…";
            await ApplyProfileValuesAsync(monitor, profile, skipInputSource: true);
        });
    }

    private void RaiseProfileCommands()
    {
        if (SaveProfileCommand is AsyncCommand save) save.RaiseCanExecuteChanged();
        if (ApplyProfileCommand is AsyncCommand apply) apply.RaiseCanExecuteChanged();
        if (DeleteProfileCommand is AsyncCommand delete) delete.RaiseCanExecuteChanged();
    }

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
        OnPropertyChanged(nameof(HasNoContinuousControls));
        OnPropertyChanged(nameof(HasInputSource));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(HasNoMonitors));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(HasNoContinuousControls));
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

public sealed class VcpControlViewModel : ObservableObject
{
    private readonly ControlViewModel _owner;
    private readonly bool _isContinuous;
    private CancellationTokenSource? _debounce;
    private double _value;
    private uint? _confirmedValue;
    private VcpValueOption? _selectedOption;
    private string? _errorText;
    private bool _isWriting;
    private bool _isReadable;

    internal VcpControlViewModel(
        ControlViewModel owner,
        byte code,
        string name,
        bool isContinuous,
        MonitorId monitorId,
        uint maximum,
        string? errorText,
        uint? currentValue,
        IReadOnlyList<VcpValueOption>? options = null)
    {
        _owner = owner;
        Code = code;
        Name = name;
        MonitorId = monitorId;
        _isContinuous = isContinuous;
        Maximum = isContinuous ? Math.Max(1u, maximum) : uint.MaxValue;
        Options = new ObservableCollection<VcpValueOption>(options ?? []);
        _errorText = errorText;
        _isReadable = currentValue.HasValue;
        if (currentValue.HasValue)
        {
            _confirmedValue = currentValue.Value;
            _value = Math.Min((double)currentValue.Value, Maximum);
            _selectedOption = Options.FirstOrDefault(option => option.Value == currentValue.Value);
            if (_selectedOption is null && !_isContinuous)
            {
                var observed = new VcpValueOption(currentValue.Value, $"当前值 0x{currentValue.Value:X2}（未映射）");
                Options.Insert(0, observed);
                _selectedOption = observed;
            }
        }
        ApplyCommand = new AsyncCommand(() => _owner.ApplyDiscreteAsync(this),
            () => !_isContinuous && IsReadable && SelectedOption is not null && !IsWriting);
    }

    public byte Code { get; }
    public string Name { get; }
    public MonitorId MonitorId { get; }
    public bool IsReadable
    {
        get => _isReadable;
        private set
        {
            if (!SetField(ref _isReadable, value)) return;
            if (ApplyCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }
    public bool IsContinuous => _isContinuous;
    public double Minimum => 0;
    public double Maximum { get; }
    public ObservableCollection<VcpValueOption> Options { get; }
    public ICommand ApplyCommand { get; }

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (!SetField(ref _value, clamped)) return;
            OnPropertyChanged(nameof(ValueText));
            if (_isContinuous && IsReadable)
            {
                _owner.OnContinuousValueChanged(this, clamped);
            }
        }
    }

    public string ValueText => IsReadable ? Math.Round(Value).ToString("0") : "—";
    public string? ErrorText
    {
        get => _errorText;
        internal set
        {
            if (!SetField(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsWriting
    {
        get => _isWriting;
        internal set
        {
            if (!SetField(ref _isWriting, value)) return;
            if (ApplyCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }

    public VcpValueOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (!SetField(ref _selectedOption, value)) return;
            if (ApplyCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }

    internal void SetReadValue(uint value)
    {
        _confirmedValue = value;
        _value = _isContinuous ? Math.Clamp(value, Minimum, Maximum) : value;
        IsReadable = true;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        if (!_isContinuous)
        {
            var option = Options.FirstOrDefault(item => item.Value == value);
            if (option is null)
            {
                option = new VcpValueOption(value, $"当前值 0x{value:X2}（未映射）");
                Options.Insert(0, option);
            }
            SelectedOption = option;
        }
    }

    internal void RestoreSelectedValue()
    {
        SelectedOption = Options.FirstOrDefault(option => option.Value == (uint)_value);
    }

    internal void RestoreConfirmedValue()
    {
        if (!_confirmedValue.HasValue) return;
        _value = _isContinuous
            ? Math.Clamp(_confirmedValue.Value, Minimum, Maximum)
            : _confirmedValue.Value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        if (!_isContinuous) RestoreSelectedValue();
    }

    internal void MarkValueUnknown()
    {
        _confirmedValue = null;
        IsReadable = false;
        if (!_isContinuous)
        {
            SelectedOption = null;
        }
        OnPropertyChanged(nameof(ValueText));
    }

    internal bool IsSelectionCurrent(uint value) => _isContinuous
        ? (uint)Math.Round(Value, MidpointRounding.AwayFromZero) == value
        : SelectedOption?.Value == value;

    internal bool TryGetConfirmedValue(out uint value)
    {
        value = _confirmedValue.GetValueOrDefault();
        return IsReadable && _confirmedValue.HasValue;
    }

    internal CancellationToken RestartDebounce()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        return _debounce.Token;
    }
}

public sealed record VcpValueOption(uint Value, string Label);

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
