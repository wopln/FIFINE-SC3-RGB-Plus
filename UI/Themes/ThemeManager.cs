using System.Windows;
using System.Windows.Media;

namespace SC3RGBController.UI.Themes;

/// <summary>
/// Swaps the app's design tokens (see Colors.xaml) for Windows system colors when
/// Windows High Contrast is on, so every surface follows the user's contrast theme.
/// Values are written directly into the given (application-level) dictionary, which
/// takes precedence over the merged Colors.xaml dictionary.
/// </summary>
public static class ThemeManager
{
    /// <summary>True while a Windows High Contrast theme is active.</summary>
    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// False in High Contrast: callers that recolor AccentBrush / AccentForegroundBrush /
    /// AccentTextBrush from the user's chosen LED color should skip doing so, keeping the
    /// system Highlight colors.
    /// </summary>
    public static bool AllowDynamicAccent => !SystemParameters.HighContrast;

    /// <summary>
    /// If High Contrast is enabled, replaces the app color/brush resource keys with
    /// system-color equivalents. Call before any window is created: most markup uses
    /// StaticResource, which is resolved once when an element loads.
    /// </summary>
    public static void ApplyHighContrastIfEnabled(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!SystemParameters.HighContrast)
        {
            return;
        }

        // Colors
        Set(resources, SystemColors.WindowColor, "WindowColor", "PanelColor", "PanelAltColor", "InputColor");
        Set(resources, SystemColors.ControlColor, "HoverColor", "PressedColor");
        Set(resources, SystemColors.WindowTextColor,
            "TextColor", "SecondaryTextColor", "MutedColor", "BorderColor", "ControlStrokeColor");
        Set(resources, SystemColors.HighlightColor, "AccentColor");

        // Surfaces
        Set(resources, SystemColors.WindowBrush,
            "WindowBrush", "PanelBrush", "PanelAltBrush", "InputBrush",
            "InfoBarBackgroundBrush", "WarningBarBackgroundBrush", "SuccessBarBackgroundBrush");
        Set(resources, SystemColors.ControlBrush, "HoverBrush", "PressedBrush");

        // Text, strokes and status indicators
        Set(resources, SystemColors.WindowTextBrush,
            "TextBrush", "SecondaryTextBrush", "MutedBrush",
            "BorderBrush", "ControlStrokeBrush",
            "ConnectedBrush", "DisconnectedBrush", "WarningBrush");

        // Focus and accent
        Set(resources, SystemColors.HighlightBrush, "FocusRingBrush", "AccentBrush");
        Set(resources, SystemColors.HighlightTextBrush, "AccentForegroundBrush");
        Set(resources, SystemColors.HotTrackBrush, "AccentTextBrush");
    }

    private static void Set(ResourceDictionary resources, object value, params string[] keys)
    {
        foreach (string key in keys)
        {
            resources[key] = value;
        }
    }

    /// <summary>
    /// Gives every window in the app a dark Windows title bar (Windows 10 2004+/11) so system-framed
    /// dialogs match the dark UI. Skipped in High Contrast, where Windows draws the frame itself.
    /// </summary>
    public static void UseDarkTitleBarsForAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window window || SystemParameters.HighContrast) return;
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int enabled = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
