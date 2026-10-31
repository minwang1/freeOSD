using System;
using System.IO;
using System.Text.Json;

namespace MonitorControl.App.Services;

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public enum AccentColor
{
    SoftBlue,
    SoftGreen,
    SoftOrange,
    Charcoal
}

public sealed record AppearanceSettings(AppearanceMode Mode, AccentColor Accent);

public sealed class AppearanceSettingsStore
{
    private readonly string _path;

    public AppearanceSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorControl", "appearance-settings.json");

    public AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return Default;
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default;
            var mode = TryReadEnum(root, "Mode", out AppearanceMode parsedMode)
                ? parsedMode
                : AppearanceMode.System;
            var accent = TryReadEnum(root, "Accent", out AccentColor parsedAccent)
                ? parsedAccent
                : AccentColor.SoftBlue;
            return new AppearanceSettings(mode, accent);
        }
        catch (IOException)
        {
            return Default;
        }
        catch (UnauthorizedAccessException)
        {
            return Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public void Save(AppearanceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new
        {
            Mode = settings.Mode.ToString(),
            Accent = settings.Accent.ToString()
        }));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static AppearanceSettings Default => new(AppearanceMode.System, AccentColor.SoftBlue);

    private static bool TryReadEnum<TEnum>(JsonElement root, string propertyName, out TEnum value)
        where TEnum : struct, Enum
    {
        if (root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && Enum.TryParse(element.GetString(), ignoreCase: true, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
