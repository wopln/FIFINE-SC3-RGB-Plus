using System.IO;
using System.Text.Json;
using SC3RGBController.Models;

namespace SC3RGBController.Services;

public static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SC3RGBController");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(FilePath);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                                   ?? new AppSettings();

            // Read the names used by builds before the persisted schema was made explicit.
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("LastHex", out JsonElement legacyHex) && legacyHex.ValueKind == JsonValueKind.String)
            {
                settings.LastHex = legacyHex.GetString() ?? settings.LastHex;
            }

            if (root.TryGetProperty("IsLightingEnabled", out JsonElement legacyLighting) &&
                (legacyLighting.ValueKind == JsonValueKind.True || legacyLighting.ValueKind == JsonValueKind.False))
            {
                settings.IsLightingEnabled = legacyLighting.GetBoolean();
            }

            if (!root.TryGetProperty("StartWithWindows", out _))
            {
                settings.StartWithWindows = true;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
