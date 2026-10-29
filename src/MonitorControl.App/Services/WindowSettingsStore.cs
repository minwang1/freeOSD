using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace MonitorControl.App.Services;

public sealed record WindowSettings(double Width, double Height, double X, double Y, bool IsProfilesTab);

public sealed class WindowSettingsStore
{
    private readonly string _path;

    public WindowSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorControl", "window-settings.json");

    public async Task<WindowSettings?> LoadAsync()
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<WindowSettings>(stream);
    }

    public void Save(Window window, bool isProfilesTab)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? window.Bounds
            : new Rect(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height);
        var value = new WindowSettings(bounds.Width, bounds.Height, window.Position.X, window.Position.Y, isProfilesTab);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, _path, overwrite: true);
    }
}
