namespace SC3RGBController.Models;

/// <summary>
/// Translates the user-facing effect speed into a safe animation frequency.
/// Output cadence remains owned by MainWindow's existing 30 Hz HID stream.
/// </summary>
public static class EffectSpeedPolicy
{
    public const int MinimumSpeed = 1;
    public const int MaximumSpeed = 100;
    public const int DefaultSpeed = 50;

    // Kept equal to the former fixed EffectSpeed so existing installations retain
    // their familiar animation timing until a user adjusts the slider.
    public const double LegacyCyclesPerSecond = 0.55;

    public static bool SupportsSpeed(LightingEffect effect) => effect is
        LightingEffect.Breathing or LightingEffect.Rainbow or
        LightingEffect.Pulse or LightingEffect.ColorCycle;

    public static int Normalize(int value) => Math.Clamp(value, MinimumSpeed, MaximumSpeed);

    public static double CyclesPerSecond(LightingEffect effect, int speed)
    {
        if (!SupportsSpeed(effect)) return 0;

        int normalized = Normalize(speed);

        // A piecewise curve preserves the legacy 0.55 cycles/s at 50%, remains
        // visibly animated at the slow end, and stays smooth at the 30 Hz output cap.
        return normalized <= DefaultSpeed
            ? 0.16 + (normalized - MinimumSpeed) * (LegacyCyclesPerSecond - 0.16) /
                (DefaultSpeed - MinimumSpeed)
            : LegacyCyclesPerSecond + (normalized - DefaultSpeed) * (0.95 - LegacyCyclesPerSecond) /
                (MaximumSpeed - DefaultSpeed);
    }
}
