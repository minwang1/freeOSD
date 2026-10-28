using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MonitorControl.Core.Abstractions;
using MonitorControl.Core.Models;

namespace MonitorControl.App.ViewModels;

public sealed class ControlViewModel : ObservableObject
{
    private static readonly byte[] ContinuousCodes = [0x10, 0x12, 0x16, 0x18, 0x1A];
    private static readonly byte[] DiscreteCodes = [0x14, 0x60];

    private readonly IMonitorBackend _backend;
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private MonitorDescriptor? _selectedMonitor;
    private bool _isLoading;
    private bool _isScanning;
    private bool _hasScanError;
    private bool _settingSelectionInternally;
    private long _loadVersion;
    private string _statusText = "就绪。";

    public ControlViewModel(IMonitorBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        RescanCommand = new AsyncCommand(RescanAsync, () => !IsScanning);
    }

    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];
    public ObservableCollection<VcpControlViewModel> ContinuousControls { get; } = [];
    public ObservableCollection<VcpControlViewModel> DiscreteControls { get; } = [];
    public ICommand RescanCommand { get; }

    /// <summary>Set by the owning window so input-source changes can be confirmed.</summary>
    public Func<VcpControlViewModel, Task<bool>>? ConfirmInputSourceAsync { get; set; }

    public MonitorDescriptor? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetField(ref _selectedMonitor, value)) return;
            RaiseSelectionProperties();
            if (!_settingSelectionInternally && value is not null)
            {
                _ = LoadSelectedMonitorAsync(value);
            }
            else if (value is null)
            {
                ClearControls();
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

            foreach (var code in ContinuousCodes)
            {
                if (!features.TryGetValue(code, out var feature)) continue;
                var (name, _, _) = Describe(code);
                try
                {
                    var value = await _backend.ReadVcpAsync(monitor.Id, code);
                    var maximum = value.Maximum is > 0 ? value.Maximum.Value
                        : feature.MaximumValue is > 0 ? feature.MaximumValue.Value : 100u;
                    continuous.Add(new VcpControlViewModel(this, code, name, true,
                        monitor.Id, maximum, null, value.Current));
                }
                catch (Exception exception)
                {
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
                    discrete.Add(new VcpControlViewModel(this, code, name, false,
                        monitor.Id, 0, null, value.Current, options));
                }
                catch (Exception exception)
                {
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
                var readback = await _backend.ReadVcpAsync(monitor.Id, control.Code);
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
                if (SelectedMonitor?.Id == monitor.Id && control.IsSelectionCurrent(value))
                {
                    control.RestoreConfirmedValue();
                    var detail = writeError is null
                        ? readError.Message
                        : $"写入错误：{writeError.Message}；读回错误：{readError.Message}";
                    control.ErrorText = detail;
                    StatusText = $"无法确认 {control.Name} 的实际值：{detail}";
                }
            }
        }
        catch (Exception exception)
        {
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

    internal bool IsSelectionCurrent(uint value) => _isContinuous
        ? (uint)Math.Round(Value, MidpointRounding.AwayFromZero) == value
        : SelectedOption?.Value == value;

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
