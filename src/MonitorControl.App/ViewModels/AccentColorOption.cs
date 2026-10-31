using Avalonia.Media;
using MonitorControl.App.Services;

namespace MonitorControl.App.ViewModels;

public sealed record AccentColorOption(AccentColor Color, string Label, IBrush Swatch);
