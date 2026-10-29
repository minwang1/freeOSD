using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MonitorControl.Core.Models;

namespace MonitorControl.App.Services;

/// <summary>Stores versioned monitor profiles in the per-user application data directory.</summary>
public sealed class ProfileStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorControl", "Profiles");
    }

    public async Task<IReadOnlyList<MonitorProfile>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var profiles = new List<MonitorProfile>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var document = await JsonSerializer.DeserializeAsync<ProfileDocument>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException($"Profile file '{Path.GetFileName(path)}' is empty.");
                if (document.SchemaVersion != CurrentSchemaVersion)
                    throw new InvalidDataException($"Profile file '{Path.GetFileName(path)}' uses unsupported schema version {document.SchemaVersion}.");
                if (string.IsNullOrWhiteSpace(document.Profile.Name))
                    throw new InvalidDataException($"Profile file '{Path.GetFileName(path)}' has no name.");
                profiles.Add(document.Profile);
            }
            return profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(MonitorProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("A profile name is required.", nameof(profile));
        var normalized = profile with { SchemaVersion = CurrentSchemaVersion, Name = profile.Name.Trim() };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var destination = Path.Combine(_directory, MakeFileName(normalized.Name) + ".json");
            var temp = destination + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new ProfileDocument(CurrentSchemaVersion, normalized), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string profileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_directory, MakeFileName(profileName.Trim()) + ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        finally { _gate.Release(); }
    }

    private static string MakeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "profile" : safe;
    }

    private sealed record ProfileDocument(int SchemaVersion, MonitorProfile Profile);
}
