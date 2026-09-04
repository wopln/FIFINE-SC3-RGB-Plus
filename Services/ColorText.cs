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
}
