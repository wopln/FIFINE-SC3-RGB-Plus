using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;

namespace SC3RGBController.UI.Controls;

/// <summary>
/// Exposes <see cref="HsvColorPicker"/> to UI Automation (Narrator, NVDA) as a
/// "color picker" with a ValuePattern: the value reads as a spoken description of the
/// color, and setting it accepts a hex code ("#RRGGBB" or "RRGGBB").
/// </summary>
public sealed class HsvColorPickerAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
{
    private const string DefaultName = "Color wheel";
    private const string DefaultHelpText =
        "Arrow keys change saturation and brightness. Page Up and Page Down change hue. Type a hex code to set the color.";

    private string? _lastValue;

    public HsvColorPickerAutomationPeer(HsvColorPicker owner) : base(owner)
    {
    }

    private HsvColorPicker Picker => (HsvColorPicker)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override string GetLocalizedControlTypeCore() => "color picker";

    protected override string GetClassNameCore() => nameof(HsvColorPicker);

    protected override bool IsKeyboardFocusableCore() => true;

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrWhiteSpace(name) ? DefaultName : name;
    }

    protected override string GetHelpTextCore()
    {
        string help = base.GetHelpTextCore();
        return string.IsNullOrWhiteSpace(help) ? DefaultHelpText : help;
    }

    public override object GetPattern(PatternInterface patternInterface)
    {
        if (patternInterface == PatternInterface.Value)
        {
            return this;
        }
        return base.GetPattern(patternInterface);
    }

    // IValueProvider

    public bool IsReadOnly => !Picker.IsEnabled;

    public string Value => Describe(Picker.SelectedColor, Picker.Hue, Picker.Saturation, Picker.Brightness);

    public void SetValue(string value)
    {
        if (!Picker.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }
        if (!TryParseHex(value, out Color color))
        {
            throw new ArgumentException("Enter a hex color such as #FF7800 or FF7800.", nameof(value));
        }
        Picker.SetColorFromAutomation(color);
    }

    /// <summary>Raises ValueProperty changed; called by the owner when SelectedColor changes.</summary>
    internal void RaiseValueChanged()
    {
        string newValue = Value;
        string? oldValue = _lastValue;
        _lastValue = newValue;
        if (oldValue == newValue)
        {
            return;
        }
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue ?? string.Empty, newValue);
        }
    }

    internal static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string hex = text.Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }
        if (hex.Length != 6 ||
            !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>E.g. "#FF7800, orange, hue 28°, saturation 100%, brightness 100%".</summary>
    internal static string Describe(Color color, double hue, double saturation, double brightness)
    {
        int h = (int)Math.Round(hue) % 360;
        int s = (int)Math.Round(saturation * 100);
        int v = (int)Math.Round(brightness * 100);
        return string.Format(
            CultureInfo.CurrentCulture,
            "#{0:X2}{1:X2}{2:X2}, {3}, hue {4}°, saturation {5}%, brightness {6}%",
            color.R, color.G, color.B, ColorName(hue, saturation, brightness), h, s, v);
    }

    /// <summary>Approximate everyday name for an HSV color.</summary>
    internal static string ColorName(double hue, double saturation, double brightness)
    {
        if (brightness < 0.12)
        {
            return "black";
        }
        if (saturation < 0.12)
        {
            if (brightness > 0.92) return "white";
            if (brightness < 0.25) return "black";
            return brightness > 0.6 ? "light gray" : "gray";
        }

        double h = (hue % 360 + 360) % 360;
        string name = h switch
        {
            < 15 => "red",
            < 40 => "orange",
            < 65 => "yellow",
            < 90 => "lime",
            < 150 => "green",
            < 170 => "teal",
            < 195 => "cyan",
            < 220 => "sky blue",
            < 255 => "blue",
            < 285 => "purple",
            < 320 => "magenta",
            < 345 => "pink",
            _ => "red"
        };

        if (brightness < 0.45) return "dark " + name;
        if (saturation < 0.35) return "pale " + name;
        return name;
    }
}
