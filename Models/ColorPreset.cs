using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SC3RGBController.Models;

public sealed class ColorPreset : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _hex = "#FF7800";
    private int _brightness = 100;
    private bool _isSelected;
    private bool _isDirty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { _name = value ?? string.Empty; OnPropertyChanged(); OnDescriptionChanged(); }
    }

    public string Hex
    {
        get => _hex;
        set { _hex = value; OnPropertyChanged(); OnDescriptionChanged(); }
    }

    public int Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnDescriptionChanged(); }
    }

    public int Order { get; set; }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            OnPropertyChanged();
            OnDescriptionChanged();
        }
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Hex : Name;

    [JsonIgnore]
    public string Subtitle => IsDirty ? "Unsaved changes" : Brightness == 100 ? Hex : $"{Hex} · {Brightness}%";

    // Spoken by screen readers for the tile; state ("selected") comes from the radio button itself.
    [JsonIgnore]
    public string AccessibleName =>
        $"{DisplayName}, {Hex}, {Brightness}% brightness" + (IsDirty ? ", unsaved changes" : string.Empty);

    [JsonIgnore]
    public string DeleteName => $"Delete preset {DisplayName}";

    // List items are announced by ToString when no name is set.
    public override string ToString() => AccessibleName;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnDescriptionChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(DeleteName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
