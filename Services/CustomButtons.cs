namespace SC3RGBController.Services;

public enum Sc3FirmwareFlavor { Unknown, Stock, Mod14, DiagnosticMod14, Mod15 }
public enum CustomButtonId { A, B, C, D }

public sealed record Sc3QueryReply(
    Sc3FirmwareFlavor Firmware,
    bool AttestationValid,
    bool CbtnPresent,
    byte CbtnVersion,
    byte KeyId,
    ushort Counter,
    byte RuntimeMode)
{
    public bool SupportsFinalCustomShortcuts =>
        Firmware == Sc3FirmwareFlavor.Mod15 && CbtnPresent && CbtnVersion == 2;

    public static bool TryParse(byte[] report, out Sc3QueryReply reply)
    {
        reply = new(Sc3FirmwareFlavor.Unknown, false, false, 0, 0, 0, 0);
        if (report is null || report.Length != HidDeviceClient.ExpectedInputLength || report[0] != 0)
            return false;

        ReadOnlySpan<byte> payload = report.AsSpan(1);
        bool mod14 = payload[..8].SequenceEqual(new byte[] { 0x53, 0x43, 0x33, 0x52, 0x11, 0x14, 0x01, 0x00 });
        bool mod15 = payload[..8].SequenceEqual(new byte[] { 0x53, 0x43, 0x33, 0x52, 0x11, 0x15, 0x01, 0x00 });
        bool cbtn = payload.Slice(0x10, 4).SequenceEqual("CBTN"u8);
        byte version = cbtn ? payload[0x14] : (byte)0;
        byte keyId = cbtn ? payload[0x15] : (byte)0;
        ushort counter = cbtn ? (ushort)(payload[0x16] | (payload[0x17] << 8)) : (ushort)0;
        byte runtimeMode = cbtn && version >= 2 ? payload[0x18] : (byte)0;

        Sc3FirmwareFlavor flavor = mod15
            ? Sc3FirmwareFlavor.Mod15
            : mod14 && cbtn && version == 1
                ? Sc3FirmwareFlavor.DiagnosticMod14
                : mod14
                    ? Sc3FirmwareFlavor.Mod14
                    : Sc3FirmwareFlavor.Unknown;

        reply = new(flavor, mod14 || mod15, cbtn, version, keyId, counter, runtimeMode);
        return true;
    }
}

public static class Sc3FirmwareClassificationPolicy
{
    public static Sc3FirmwareFlavor Resolve(bool validatedProfile, bool legacyModInstalled, Sc3QueryReply? queryReply)
    {
        if (!validatedProfile) return Sc3FirmwareFlavor.Unknown;
        if (queryReply is not null && queryReply.Firmware != Sc3FirmwareFlavor.Unknown)
            return queryReply.Firmware;
        return legacyModInstalled ? Sc3FirmwareFlavor.Mod14 : Sc3FirmwareFlavor.Stock;
    }
}
public sealed record MixerFirmwarePresentation(
    string Current,
    string Latest,
    string Status,
    string Description,
    bool UpdateAvailable,
    bool CustomButtonsAvailable)
{
    public const string LatestLabel = "RGB+ Firmware 1.5";

    public static MixerFirmwarePresentation Create(
        bool connected,
        bool validatedProfile,
        Sc3FirmwareFlavor flavor,
        bool finalMod15Capability)
    {
        if (!connected)
            return new("Not detected", LatestLabel, "Connect your SC3", "Connect a supported FIFINE SC3 to check mixer firmware.", false, false);
        if (!validatedProfile)
            return new("Unsupported", LatestLabel, "Unsupported device", "This SC3 profile is not supported for firmware updates.", false, false);

        return flavor switch
        {
            Sc3FirmwareFlavor.Mod15 when finalMod15Capability =>
                new(LatestLabel, LatestLabel, "Up to date", "Firmware 1.5 includes support for Custom Button Shortcuts.", false, true),
            Sc3FirmwareFlavor.Mod15 =>
                new(LatestLabel, LatestLabel, "Verification failed", "Firmware 1.5 was detected, but the Custom Button capability could not be verified.", false, false),
            Sc3FirmwareFlavor.Mod14 =>
                new("RGB+ Firmware 1.4", LatestLabel, "Update available", "Firmware 1.5 adds support for Custom Button Shortcuts.", true, false),
            Sc3FirmwareFlavor.DiagnosticMod14 =>
                new("Diagnostic RGB+ Firmware 1.4", LatestLabel, "Diagnostic firmware detected", "Install the production firmware before using Custom Button Shortcuts.", false, false),
            Sc3FirmwareFlavor.Stock =>
                new("Original Stock V22", LatestLabel, "RGB+ firmware not installed", "Install RGB+ Firmware 1.5 to enable RGB+ and Custom Button Shortcuts.", true, false),
            _ =>
                new("Unknown", LatestLabel, "Unable to verify", "Reconnect the SC3 and try again.", false, false)
        };
    }
}
public static class CustomShortcutStatusPolicy
{
    public const string Stock = "Original SC3 button behavior is active.";
    public const string Active = "Custom A–D application shortcuts are active.";
    public const string Unavailable = "Shortcuts unavailable — Original SC3 behavior is active.";

    public static string For(CustomShortcutRuntimeState state) => state switch
    {
        CustomShortcutRuntimeState.Active => Active,
        CustomShortcutRuntimeState.Unavailable => Unavailable,
        _ => Stock
    };
}

public sealed class CustomButtonEventTracker
{
    private ushort? _baseline;

    public void Reset() => _baseline = null;
    public void SetBaseline(ushort counter) => _baseline = counter;

    public bool TryAccept(byte keyId, ushort counter, out CustomButtonId button)
    {
        button = default;
        if (_baseline is null)
        {
            _baseline = counter;
            return false;
        }
        if (_baseline.Value == counter)
            return false;

        _baseline = counter;
        return TryMapKey(keyId, out button);
    }

    public static bool TryMapKey(byte keyId, out CustomButtonId button)
    {
        switch (keyId)
        {
            case 19: button = CustomButtonId.A; return true;
            case 12: button = CustomButtonId.B; return true;
            case 18: button = CustomButtonId.C; return true;
            case 13: button = CustomButtonId.D; return true;
            default: button = default; return false;
        }
    }
}
