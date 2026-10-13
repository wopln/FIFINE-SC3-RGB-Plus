using System.Globalization;
using System.Windows.Media;

namespace SC3RGBController.Services;

public static class ColorText
{
    public static bool TryParseHex(string? input, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string text = input.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static bool TryParseRgb(string? redText, string? greenText, string? blueText, out Color color)
    {
        color = default;
        if (!byte.TryParse(redText, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte red) ||
            !byte.TryParse(greenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte green) ||
            !byte.TryParse(blueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte blue))
        {
            return false;
        }

        color = Color.FromRgb(red, green, blue);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // WCAG 2.x relative luminance and contrast ratio.
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Text/icon color for content drawn on top of the user's selected color.
    public static Color ForegroundOn(Color background) =>
        Contrast(background, Colors.White) >= Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

    // The selected color lightened just enough to stay readable (4.5:1) on the
    // app's near-black panels, for accent text, outlines and focus rings.
    public static Color VisibleOnDark(Color color, Color? surface = null)
    {
        Color panel = surface ?? Color.FromRgb(0x09, 0x09, 0x09);
        for (double t = 0; t <= 1.0; t += 0.05)
        {
            Color mixed = Color.FromRgb(
                (byte)Math.Round(color.R + (255 - color.R) * t),
                (byte)Math.Round(color.G + (255 - color.G) * t),
                (byte)Math.Round(color.B + (255 - color.B) * t));
            if (Contrast(mixed, panel) >= 4.5) return mixed;
        }
        return Colors.White;
    }
}
