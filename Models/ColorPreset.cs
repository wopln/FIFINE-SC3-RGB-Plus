using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;

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
        set { _name = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Hex
    {
        get => _hex;
        set { _hex = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(BorderHex)); }
    }

    public int Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
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
            OnPropertyChanged(nameof(BorderHex));
            OnPropertyChanged(nameof(CardBorderThickness));
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
            OnPropertyChanged(nameof(DirtyVisibility));
        }
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Hex : Name;

    [JsonIgnore]
    public string BorderHex => IsSelected ? Hex : "#343434";

    [JsonIgnore]
    public Thickness CardBorderThickness => IsSelected ? new Thickness(2) : new Thickness(1);

    [JsonIgnore]
    public Visibility DirtyVisibility => IsDirty ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
