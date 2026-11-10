using System.Text.Json;
using Bridgewiz.AppUpdate.Avalonia;

namespace Ui.Updates;

/// <summary>
/// The user's preferences, in a small JSON file in the user's application data. Reading or writing it never fails
/// the app: a missing or broken file gives the defaults.
/// </summary>
public sealed class UserSettings : IUpdateSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string? _store;

    public static string DefaultStore => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compressor", "settings.json");

    /// <summary>Look for a newer version when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The version the user chose to skip; the startup check doesn't offer it again (a manual check does).</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Another update server, for testing a release; null for the usual one. Set by editing the file.</summary>
    public string? UpdateServer { get; set; }

    bool IUpdateSettings.CheckOnStartup => CheckForUpdates;

    /// <param name="store">The JSON file, or null to keep the settings in memory only.</param>
    public static UserSettings Load(string? store)
    {
        UserSettings settings = new();
        if (store is not null && File.Exists(store))
        {
            try
            {
                settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(store), Json) ?? new UserSettings();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        settings._store = store;
        return settings;
    }

    public void Save()
    {
        if (_store is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
            File.WriteAllText(_store, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
