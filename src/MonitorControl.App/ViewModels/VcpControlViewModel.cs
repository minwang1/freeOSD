using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MonitorControl.Core.Models;

namespace MonitorControl.App.ViewModels;

public sealed class VcpControlViewModel : ObservableObject
{
    private readonly ControlViewModel _owner;
    private readonly string _name;
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
        _name = name;
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
    public string Name => _name;
    public string DisplayName => Localize(_name);
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
        get => _errorText is null ? null : Localize(_errorText);
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
