using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonitorControl.Core.Models;

namespace MonitorControl.App.Services;

public sealed class AutomationRuleStore
{
    private const int CurrentSchemaVersion = 3;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutomationRuleStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorControl", "automation-rules.json");

    public async Task<ApplicationAutomationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new ApplicationAutomationSettings([], null, false);
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<RuleDocument>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Automation rule file is empty.");
            if (document.SchemaVersion == 1)
                return new ApplicationAutomationSettings(document.Rules ?? [], null, false);
            if (document.SchemaVersion == 2)
                return new ApplicationAutomationSettings(document.Rules ?? [], document.DefaultProfileName, false);
            if (document.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Unsupported automation rule schema version {document.SchemaVersion}.");
            return new ApplicationAutomationSettings(document.Rules ?? [], document.DefaultProfileName, document.IsEnabled);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ApplicationAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, new RuleDocument(CurrentSchemaVersion, settings.Rules, settings.DefaultProfileName, settings.IsEnabled), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private sealed record RuleDocument(int SchemaVersion, IReadOnlyList<ApplicationProfileRule>? Rules, string? DefaultProfileName = null, bool IsEnabled = false);
}
