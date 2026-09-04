using System.Text.Json.Serialization;

namespace SC3RGBController.Models;

public sealed class AppSettings
{
    [JsonPropertyName("Color")]
    public string LastHex { get; set; } = "#FF7800";

    [JsonPropertyName("Red")]
    public int Red { get; set; } = 255;

    [JsonPropertyName("Green")]
    public int Green { get; set; } = 120;

    [JsonPropertyName("Blue")]
    public int Blue { get; set; }

    [JsonPropertyName("Brightness")]
    public int Brightness { get; set; } = 100;

    [JsonPropertyName("LightingEnabled")]
    public bool IsLightingEnabled { get; set; } = true;

    [JsonPropertyName("Preset")]
    public string? SelectedPresetName { get; set; }

    [JsonPropertyName("Effect")]
    public string Effect { get; set; } = nameof(LightingEffect.Static);

    public string? SelectedPresetId { get; set; }

    [JsonPropertyName("StartWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    public bool PresetsInitialized { get; set; }
    public List<ColorPreset> Presets { get; set; } = [];
}
