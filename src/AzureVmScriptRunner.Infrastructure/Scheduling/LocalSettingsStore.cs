using System.Text.Json;

namespace AzureVmScriptRunner.Infrastructure.Scheduling;

public sealed record LocalSettings
{
    /// <summary>Subscription hosting the Automation account used for scheduling.</summary>
    public string? AutomationSubscriptionId { get; init; }

    public string? AutomationRegion { get; init; }

    /// <summary>Resource group name (CAF abbreviation 'rg-'). Editable before provisioning.</summary>
    public string ResourceGroupName { get; init; } = "rg-avsr-automation";

    /// <summary>Automation account name (CAF abbreviation 'aa-').</summary>
    public string AutomationAccountName { get; init; } = "aa-avsr";

    /// <summary>Tags applied to created resources, as "key=value" pairs.</summary>
    public string TagsRaw { get; init; } = "managedBy=AzureVmScriptRunner";

    /// <summary>
    /// Optional user-assigned managed identity (full resource ID) to use instead of a
    /// system-assigned one. Survives account deletion; must be cleaned up manually.
    /// </summary>
    public string? UserAssignedIdentityResourceId { get; init; }

    public IReadOnlyDictionary<string, string> ParseTags()
    {
        var tags = new Dictionary<string, string>();
        foreach (var pair in TagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                tags[parts[0]] = parts[1];
            }
        }

        return tags;
    }
}

/// <summary>Small JSON settings file next to the local database.</summary>
public sealed class LocalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public LocalSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzureVmScriptRunner", "settings.json");
    }

    public LocalSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(_path), JsonOptions)
                    ?? new LocalSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return new LocalSettings();
    }

    public void Save(LocalSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
