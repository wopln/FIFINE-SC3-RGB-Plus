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
            return File.Exists(FilePath)
                ? Deserialize(File.ReadAllText(FilePath))
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static AppSettings Deserialize(string json)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                               ?? new AppSettings();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("LastHex", out JsonElement legacyHex) && legacyHex.ValueKind == JsonValueKind.String)
            settings.LastHex = legacyHex.GetString() ?? settings.LastHex;

        if (root.TryGetProperty("IsLightingEnabled", out JsonElement legacyLighting) &&
            legacyLighting.ValueKind is JsonValueKind.True or JsonValueKind.False)
            settings.IsLightingEnabled = legacyLighting.GetBoolean();

        if (!root.TryGetProperty("StartWithWindows", out _))
            settings.StartWithWindows = true;
        if (!root.TryGetProperty("AutomaticallyCheckForUpdates", out _))
            settings.AutomaticallyCheckForUpdates = true;
        if (!root.TryGetProperty("CustomShortcutsEnabled", out _))
            settings.CustomShortcutsEnabled = false;

        settings.BreathingSpeed = EffectSpeedPolicy.Normalize(settings.BreathingSpeed);
        settings.RainbowSpeed = EffectSpeedPolicy.Normalize(settings.RainbowSpeed);
        settings.PulseSpeed = EffectSpeedPolicy.Normalize(settings.PulseSpeed);
        settings.ColorCycleSpeed = EffectSpeedPolicy.Normalize(settings.ColorCycleSpeed);
        return settings;
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, Serialize(settings));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
