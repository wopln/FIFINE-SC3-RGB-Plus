namespace SC3FirmwareTool.Core;

public static class ReleasePolicy
{
    public const string NativeUpdaterReleaseTier = "ProductionStable";
    public const string FirmwareFileName = "SC3-V22-RGB-Mod-1.5-CustomButtons.mva";
    public const string FirmwareSha256 = "589b2fcb590b999c905693df6aba6a343343ac6a8241b4aa9802853a72fa525b";
    public const long FirmwareSize = 1_726_821;
    public const ushort FirmwareCrc16 = 0xC12C;
    public const string BuildId = "SC3R-11150100";
    public const byte CbtnVersion = 2;
    public const string OfficialVersion = "1.33.5";
    public static readonly byte[] Attestation = Convert.FromHexString("5343335211150100");

    public const ushort NormalVid = 0x3142;
    public const ushort NormalPid = 0x0C33;
    public const ushort BootVid = 0x0000;
    public const ushort BootPid = 0x2244;
    public const string Manufacturer = "MV-SILICON";
    public const string Product = "fifine SC3";
    public const string Serial = "20190808";
    public const ushort BcdDevice = 0x0100;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 0x55AA;
    public const string ValidatedHidInstance = "a&2911e28a&0&0000";
    public const string ValidatedBootHidInstance = "8&2b96d23b&0&0000";
}

public static class LegacyMod14Policy
{
    public const string FirmwareFileName = "SC3-V22-RGB-Mod-1.4-Attestation-Candidate.mva";
    public const string FirmwareSha256 = "fb763b1f4e318b529f932897b63b723545f75f090fc220d9dd666198e73955b8";
    public const long FirmwareSize = 1_726_821;
    public const string BuildId = "SC3R-11140100";
    public static readonly byte[] Attestation = Convert.FromHexString("5343335211140100");
}

public static class Mod15CandidatePolicy
{
    public const string FirmwareFileName = ReleasePolicy.FirmwareFileName;
    public const string FirmwareSha256 = ReleasePolicy.FirmwareSha256;
    public const long FirmwareSize = ReleasePolicy.FirmwareSize;
    public const ushort FirmwareCrc16 = ReleasePolicy.FirmwareCrc16;
    public const string BuildId = ReleasePolicy.BuildId;
    public static readonly byte[] Attestation = ReleasePolicy.Attestation;
}

public enum InstalledFirmwareFlavor
{
    Unknown,
    StockV22,
    Mod14,
    Mod15
}

public sealed record FirmwareIdentity(InstalledFirmwareFlavor Flavor, bool CbtnPresent, byte CbtnVersion)
{
    public bool IsProductionCurrent =>
        Flavor == InstalledFirmwareFlavor.Mod15 && CbtnPresent && CbtnVersion == ReleasePolicy.CbtnVersion;
}

public static class FirmwareIdentityPolicy
{
    public static FirmwareIdentity ParseAttestationReport(ReadOnlySpan<byte> report)
    {
        if (report.Length < 0x15)
            return new(InstalledFirmwareFlavor.Unknown, false, 0);

        InstalledFirmwareFlavor flavor = report[..8].SequenceEqual(ReleasePolicy.Attestation)
            ? InstalledFirmwareFlavor.Mod15
            : report[..8].SequenceEqual(LegacyMod14Policy.Attestation)
                ? InstalledFirmwareFlavor.Mod14
                : InstalledFirmwareFlavor.StockV22;

        bool cbtn = report.Slice(0x10, 4).SequenceEqual("CBTN"u8);
        byte version = cbtn ? report[0x14] : (byte)0;
        return new(flavor, cbtn, version);
    }

    public static bool NeedsProductionInstall(FirmwareIdentity identity) =>
        identity.Flavor is InstalledFirmwareFlavor.StockV22 or InstalledFirmwareFlavor.Mod14;
}
public static class StockRecoveryPolicy
{
    public const string FirmwareFileName = "SC3_V22_recovery.MVA";
    public const string FirmwareSha256 = "01a282431c3d82ffd64aa7095f8e151893f459094e2c5ee08010dba430cffcdd";
    public const long FirmwareSize = 1_726_821;
    public const string BuildId = "STOCK-V22";
    public const string Confirmation = "SC3-STOCK-V22-RECOVERY";

    public static bool IsConfirmed(string? value) =>
        string.Equals(value, Confirmation, StringComparison.Ordinal);
}

public static class VendorTransferTiming
{
    // Proven from the extracted vendor helper erase loop:
    // - first GET_REPORT is immediate after chiperas
    // - exactly 16 GET_REPORT attempts are made
    // - unfinished positive erase statuses are followed by Sleep(500)
    // - failed HID reads in the captures complete after about 5.0 seconds
    // - after the erase loop finishes, Sleep(1000) runs before metadata/cxxx
    public const int ErasePollCount = 16;
    public const int ErasePollMilliseconds = 500;
    public const int EraseReadTimeoutMilliseconds = 6500;
    public const int PostEraseSettleMilliseconds = 1000;

    // Proven from the extracted vendor helper: after codedata/constdat prepare,
    // Sleep(sectionSectorCount * 3) is executed before the first data report.
    public const int SectionPrepareMillisecondsPerSector = 3;

    public static TimeSpan ErasePollDelay => TimeSpan.FromMilliseconds(ErasePollMilliseconds);
    public static TimeSpan PostEraseSettleDelay => TimeSpan.FromMilliseconds(PostEraseSettleMilliseconds);

    public static int SectorCount(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        return (length + 4095) / 4096;
    }

    public static TimeSpan SectionPrepareDelay(int length) =>
        TimeSpan.FromMilliseconds((long)SectorCount(length) * SectionPrepareMillisecondsPerSector);
}

public static class ErasePollPolicy
{
    // Vendor-compatible erase GET_REPORT expiry can surface through Windows as
    // ERROR_GEN_FAILURE (31) or ERROR_SEM_TIMEOUT (121), depending on the HID/
    // USB stack path. Both are tolerated only during erase status polling and
    // only after at least one structurally valid positive progress reply.
    // No stale bytes are consumed and no firmware data is sent until the later
    // metadata/cxxx exchange returns an exact valid section selection.
    public static bool IsVendorNoStatusFailure(int previousProgress, FirmwareUpdateException cause) =>
        previousProgress > 0 && !cause.TimedOut && cause.WindowsError is 31 or 121;

    // The vendor helper always performs the full 16-attempt erase polling
    // window. Successful captures show later GET_REPORT attempts continuing
    // after the first no-status completion. Do not shorten that window: the
    // extra elapsed time is part of the proven erase/settle behavior even when
    // those later reads return no usable status.
    public static bool AllowsDeferredMetadataValidation(int previousProgress, int pollsAttempted, int noStatusFailures) =>
        previousProgress > 0 &&
        pollsAttempted == VendorTransferTiming.ErasePollCount &&
        noStatusFailures > 0;
}

public static class SectorAckPolicy
{
    public static void Validate(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual,
        string sectionName, int sector, int ordinal)
    {
        if (expected.Length != 256 || actual.Length != 256)
            throw new FirmwareUpdateException(
                $"Malformed sector ACK for {sectionName} sector {sector} (ACK {ordinal}): expected 256 bytes, received {actual.Length}.", true);

        if (!actual.SequenceEqual(expected))
            throw new FirmwareUpdateException(
                $"Wrong or stale sector ACK for {sectionName} sector {sector} (ACK {ordinal}).", true);
    }

    public static FirmwareUpdateException ReadFailure(string sectionName, int sector, int ordinal,
        FirmwareUpdateException cause) =>
        new($"Sector ACK read failed for {sectionName} sector {sector} (ACK {ordinal}): {cause.Message}",
            true, cause, windowsError: cause.WindowsError, timedOut: cause.TimedOut);
}

public enum UpdaterState
{
    Idle, ValidatingDevice, ValidatingFirmware, EnteringBootloader,
    WaitingForBootloader, BootloaderConnected, PreparingUpdate, Erasing,
    Transferring, Finalizing, WaitingForReboot, VerifyingDevice,
    Success, Failed, SetupFailedDeviceHealthy, SetupFailedBootloaderAvailable,
    RecoveryRequired, SetupSucceeded, RestoreFailedDeviceHealthy,
    RestoreFailedBootloaderAvailable, RestoreRecoveryRequired, RestoreSucceeded
}

public sealed record UpdateProgress(UpdaterState State, int Percent, string Message,
    int CurrentBlock = 0, int TotalBlocks = 0, long BytesTransferred = 0,
    TimeSpan Elapsed = default);

public sealed record DeviceStatus(bool Present, bool ValidatedProfile, bool ModInstalled,
    string Message, string? Path = null, string? Topology = null);

public sealed record DryRunResult(bool Passed, int DataBlocks, int SectorAcks,
    int OutboundReports, string FirmwareSha256, string Summary);

public enum RestoreStartMode
{
    Normal,
    Bootloader
}

public sealed record RestoreDetection(bool CanRestore, RestoreStartMode? StartMode,
    bool RecoveryMode, string Message, DeviceStatus NormalStatus, bool BootloaderPresent);

public sealed record RestoreDryRunResult(bool Passed, RestoreStartMode StartMode,
    int DataBlocks, int SectorAcks, int OutboundReports, string FirmwareSha256,
    string Summary);

public static class RestoreFlowPolicy
{
    public static RestoreDetection ClassifyStart(DeviceStatus normal, bool bootloaderPresent)
    {
        if (normal.Present && bootloaderPresent)
            return new(false, null, false, "Ambiguous SC3 state: normal device and recovery bootloader are both present.", normal, true);
        if (normal.ValidatedProfile && !bootloaderPresent)
            return new(true, RestoreStartMode.Normal, false, "Validated SC3 ready for stock restore.", normal, false);
        if (!normal.Present && bootloaderPresent)
            return new(true, RestoreStartMode.Bootloader, true, "SC3 detected in recovery mode.", normal, true);
        return new(false, null, false, normal.Message, normal, bootloaderPresent);
    }
}

public static class FirmwareOutcomeClassifier
{
    public static UpdaterState ClassifyPostFailure(bool destructive, DeviceStatus normal, bool bootPresent)
    {
        if (normal.ValidatedProfile && !bootPresent)
            return normal.ModInstalled ? UpdaterState.SetupSucceeded : UpdaterState.SetupFailedDeviceHealthy;
        if (bootPresent && !normal.Present)
            return UpdaterState.SetupFailedBootloaderAvailable;
        return destructive ? UpdaterState.RecoveryRequired : UpdaterState.Failed;
    }

    public static UpdaterState ClassifyRestorePostFailure(bool destructive, DeviceStatus normal, bool bootPresent)
    {
        if (normal.ValidatedProfile && !bootPresent)
            return UpdaterState.RestoreFailedDeviceHealthy;
        if (bootPresent && !normal.Present)
            return UpdaterState.RestoreFailedBootloaderAvailable;
        return destructive ? UpdaterState.RestoreRecoveryRequired : UpdaterState.Failed;
    }
}

public static class StockVerificationPolicy
{
    public static bool IsVerifiedStock(DeviceStatus normal, bool bootloaderPresent) =>
        normal.Present && normal.ValidatedProfile && !normal.ModInstalled && !bootloaderPresent;
}

public static class FirmwarePresentationPolicy
{
    public static string ReadyLabel(DeviceStatus? normal, bool recoveryMode, bool operationActive)
    {
        if (operationActive) return "Firmware operation in progress";
        if (recoveryMode) return "Recovery mode";
        if (normal?.ValidatedProfile == true && normal.ModInstalled) return "RGB Ready";
        if (normal?.ValidatedProfile == true) return "RGB setup required";
        return "RGB unavailable";
    }
}

public sealed class FirmwareUpdateException : Exception
{
    public FirmwareUpdateException(string message, bool destructive = false, Exception? inner = null,
        UpdaterState? outcome = null, int? windowsError = null, bool timedOut = false) : base(message, inner)
    {
        Destructive = destructive;
        Outcome = outcome ?? (destructive ? UpdaterState.RecoveryRequired : UpdaterState.Failed);
        WindowsError = windowsError;
        TimedOut = timedOut;
    }

    public bool Destructive { get; }
    public UpdaterState Outcome { get; }
    public int? WindowsError { get; }
    public bool TimedOut { get; }
}
