using SC3FirmwareTool.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string firmware = Path.Combine(root, "firmware", "candidates", "mod14", ReleasePolicy.FirmwareFileName);
MvaPackage package = MvaPackage.LoadApproved(firmware);
var plan = ProtocolPlan.Build(package);
void Require(bool value, string message) { if (!value) throw new Exception(message); }
Require(package.Sha256 == ReleasePolicy.FirmwareSha256, "hash");
Require(plan.Count(x => x.Operation == ProtocolOperation.Write && x.DataBlock > 0) == 6496, "data blocks");
Require(plan.Count(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK")) == 406, "acks");
Require(plan.Count(x => x.Operation is ProtocolOperation.Write or ProtocolOperation.BootFeatureWrite) == 6502, "outbound");
Require(plan.First(x => x.Operation == ProtocolOperation.Write).Bytes!.AsSpan(0,8).SequenceEqual("chiperas"u8), "erase");
Require(plan.Any(x => x.Bytes?.AsSpan(0,8).SequenceEqual("codedata"u8) == true), "code");
Require(plan.Any(x => x.Bytes?.AsSpan(0,8).SequenceEqual("constdat"u8) == true), "const");
Require(plan.Any(x => x.Bytes?.AsSpan(0,6).SequenceEqual("upinfo"u8) == true), "final");
Require(VendorTransferTiming.SectorCount(package.CodeLength) == 293, "code sectors");
Require(VendorTransferTiming.SectorCount(package.ConstLength) == 113, "const sectors");
Require(VendorTransferTiming.ErasePollDelay == TimeSpan.FromMilliseconds(500), "vendor erase poll pacing");
Require(VendorTransferTiming.PostEraseSettleDelay == TimeSpan.FromMilliseconds(1000), "vendor post-erase settle pacing");
Require(VendorTransferTiming.ErasePollCount == 16, "vendor erase poll count");
Require(VendorTransferTiming.EraseReadTimeoutMilliseconds == 6500, "vendor erase read timeout margin");
var erase31 = new FirmwareUpdateException("GET_REPORT failed (Windows 31).", true, windowsError: 31);
var erase121 = new FirmwareUpdateException("GET_REPORT failed (Windows 121).", true, windowsError: 121);
var erase5 = new FirmwareUpdateException("GET_REPORT failed (Windows 5).", true, windowsError: 5);
var eraseTimeout = new FirmwareUpdateException("GET_REPORT timed out.", true, timedOut: true);
Require(ErasePollPolicy.IsVendorNoStatusFailure(16, erase31), "erase Windows 31 after progress must be no-status eligible");
Require(ErasePollPolicy.IsVendorNoStatusFailure(16, erase121), "erase Windows 121 after progress must be no-status eligible");
Require(!ErasePollPolicy.IsVendorNoStatusFailure(0, erase31), "erase Windows 31 before progress must fail closed");
Require(!ErasePollPolicy.IsVendorNoStatusFailure(0, erase121), "erase Windows 121 before progress must fail closed");
Require(!ErasePollPolicy.IsVendorNoStatusFailure(16, erase5), "unexpected erase Windows error must fail closed");
Require(!ErasePollPolicy.IsVendorNoStatusFailure(16, eraseTimeout), "erase host timeout must fail closed");
Require(ErasePollPolicy.AllowsDeferredMetadataValidation(16, 16, 12), "captured erase fallback shape");
Require(!ErasePollPolicy.AllowsDeferredMetadataValidation(16, 5, 1), "erase fallback must not shorten vendor poll window");
Require(!ErasePollPolicy.AllowsDeferredMetadataValidation(16, 15, 11), "erase fallback requires all vendor polls");
Require(!ErasePollPolicy.AllowsDeferredMetadataValidation(0, 16, 16), "no positive erase progress must fail closed");
Require(VendorTransferTiming.SectionPrepareDelay(package.CodeLength) == TimeSpan.FromMilliseconds(879), "code vendor pacing");
Require(VendorTransferTiming.SectionPrepareDelay(package.ConstLength) == TimeSpan.FromMilliseconds(339), "const vendor pacing");
Require(((package.ConstLength + 4095) / 4096) * 16 == 1808, "full 1808-report const model");

int codePrepare = plan.ToList().FindIndex(x => x.Operation == ProtocolOperation.Write && x.Bytes?.AsSpan(0,8).SequenceEqual("codedata"u8) == true);
int constPrepare = plan.ToList().FindIndex(x => x.Operation == ProtocolOperation.Write && x.Bytes?.AsSpan(0,8).SequenceEqual("constdat"u8) == true);
Require(codePrepare >= 0 && constPrepare > codePrepare, "Code to Const transition");
Require(plan.Skip(codePrepare + 1).Take(constPrepare - codePrepare - 1).Count(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK")) == 293, "Code ACK count before Const");

byte[] expectedAck = plan.First(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK")).Bytes!;
SectorAckPolicy.Validate(expectedAck, (byte[])expectedAck.Clone(), "codedata", 1, 1);
foreach (var bad in new[] { expectedAck[..255], new byte[256], expectedAck.Concat(new byte[] { 0 }).ToArray() })
{
    try { SectorAckPolicy.Validate(expectedAck, bad, "codedata", 1, 1); throw new Exception("bad ACK accepted"); }
    catch (FirmwareUpdateException) { }
}
byte[] staleAck = (byte[])expectedAck.Clone(); staleAck[0] ^= 1;
try { SectorAckPolicy.Validate(expectedAck, staleAck, "codedata", 1, 1); throw new Exception("stale ACK accepted"); }
catch (FirmwareUpdateException) { }

foreach (var failure in new[]
{
    new FirmwareUpdateException("GET_REPORT failed (Windows 31).", true, windowsError: 31),
    new FirmwareUpdateException("GET_REPORT failed (Windows 5).", true, windowsError: 5),
    new FirmwareUpdateException("GET_REPORT timed out.", true, timedOut: true)
})
{
    FirmwareUpdateException wrapped = SectorAckPolicy.ReadFailure("constdat", 80, 80, failure);
    Require(wrapped.Destructive, "ACK I/O must fail closed");
    Require(wrapped.WindowsError == failure.WindowsError && wrapped.TimedOut == failure.TimedOut, "ACK I/O classification preserved");
}

byte[] constOnly = (byte[])package.Metadata.Clone();
Convert.FromHexString("FF55FFFFFF").CopyTo(constOnly, 8);
byte[] codeAndConst = (byte[])package.Metadata.Clone();
Convert.FromHexString("5555FFFFFF").CopyTo(codeAndConst, 8);
Require(ProtocolPlan.ValidateSectionSelection(package.Metadata, constOnly) == FirmwareSectionSelection.ConstOnly, "const-only selection");
Require(ProtocolPlan.ValidateSectionSelection(package.Metadata, codeAndConst) == FirmwareSectionSelection.CodeAndConst, "code+const selection");
Require(FirmwareOutcomeClassifier.ClassifyPostFailure(true,
    new DeviceStatus(true, true, false, "stock"), false) == UpdaterState.SetupFailedDeviceHealthy, "healthy stock outcome");
Require(FirmwareOutcomeClassifier.ClassifyPostFailure(true,
    new DeviceStatus(false, false, false, "boot"), true) == UpdaterState.SetupFailedBootloaderAvailable, "bootloader available outcome");
Require(FirmwareOutcomeClassifier.ClassifyPostFailure(true,
    new DeviceStatus(false, false, false, "missing"), false) == UpdaterState.RecoveryRequired, "recovery outcome");
Require(FirmwareOutcomeClassifier.ClassifyPostFailure(true,
    new DeviceStatus(true, true, true, "mod"), false) == UpdaterState.SetupSucceeded, "setup succeeded outcome");
foreach (string invalidFlags in new[] { "FFFFFFFFFF", "55FFFFFFFF", "FFFF55FFFF", "555555FFFF" })
{
    byte[] invalid = (byte[])package.Metadata.Clone();
    Convert.FromHexString(invalidFlags).CopyTo(invalid, 8);
    try { ProtocolPlan.ValidateSectionSelection(package.Metadata, invalid); throw new Exception("invalid selection accepted: " + invalidFlags); }
    catch (FirmwareUpdateException) { }
}
byte[] alteredMetadata = (byte[])codeAndConst.Clone(); alteredMetadata[40] ^= 1;
try { ProtocolPlan.ValidateSectionSelection(package.Metadata, alteredMetadata); throw new Exception("altered selection metadata accepted"); }
catch (FirmwareUpdateException) { }

byte[] corrupt = (byte[])package.Data.Clone(); corrupt[500] ^= 1;
string temporary = Path.Combine(Path.GetTempPath(), ReleasePolicy.FirmwareFileName);
File.WriteAllBytes(temporary, corrupt);
try { MvaPackage.LoadApproved(temporary); throw new Exception("corrupt package accepted"); }
catch (FirmwareUpdateException) { }
finally { File.Delete(temporary); }
Console.WriteLine("PASS: package/profile, full 16-poll vendor erase window after captured 16/30 + Windows 31/121 with exact metadata gate, 500ms pacing + 6.5s read allowance + 1000ms settle + 879ms/339ms section pacing + immediate post-SET sector ACK read, sector error31/timeout fail-closed, Code->Const transition, 1808 Const reports, 6496 full-session data reports, 406 ACKs, finalization, corruption rejection");
// Restore Original Firmware regression suite.
string stockFirmware = Path.Combine(root, "firmware", "recovery", StockRecoveryPolicy.FirmwareFileName);
MvaPackage stockPackage = MvaPackage.LoadStockRecovery(stockFirmware);
Require(stockPackage.Sha256 == StockRecoveryPolicy.FirmwareSha256, "1 stock recovery SHA accepted");
Require(stockPackage.Data.LongLength == StockRecoveryPolicy.FirmwareSize, "1 stock recovery size accepted");
var stockPlan = ProtocolPlan.Build(stockPackage);
Require(stockPlan.Count(x => x.Operation == ProtocolOperation.Write && x.DataBlock > 0) == 6496, "stock data plan");
Require(stockPlan.Count(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK")) == 406, "stock ACK plan");
Require(stockPlan.Any(x => x.Bytes?.AsSpan(0, 8).SequenceEqual("chiperas"u8) == true), "stock erase plan");
Require(stockPlan.Any(x => x.Bytes?.AsSpan(0, 8).SequenceEqual("codedata"u8) == true), "stock code plan");
Require(stockPlan.Any(x => x.Bytes?.AsSpan(0, 8).SequenceEqual("constdat"u8) == true), "stock const plan");
Require(stockPlan.Any(x => x.Bytes?.AsSpan(0, 6).SequenceEqual("upinfo"u8) == true), "stock finalization plan");

string stockTempDir = Path.Combine(Path.GetTempPath(), "sc3-stock-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stockTempDir);
string corruptStockPath = Path.Combine(stockTempDir, StockRecoveryPolicy.FirmwareFileName);
byte[] corruptStock = (byte[])stockPackage.Data.Clone();
corruptStock[700] ^= 1;
File.WriteAllBytes(corruptStockPath, corruptStock);
try { MvaPackage.LoadStockRecovery(corruptStockPath); throw new Exception("2 wrong recovery SHA accepted"); }
catch (FirmwareUpdateException ex) { Require(!ex.Destructive, "2 bad stock package rejection must be pre-destructive"); }
finally { Directory.Delete(stockTempDir, true); }

DeviceStatus validatedMod = new(true, true, true, "mod");
DeviceStatus validatedStock = new(true, true, false, "stock");
DeviceStatus unsupported = new(true, false, false, "unsupported");
DeviceStatus missing = new(false, false, false, "missing");

RestoreDetection unsupportedStart = RestoreFlowPolicy.ClassifyStart(unsupported, false);
Require(!unsupportedStart.CanRestore, "3 unsupported device rejected");
RestoreDetection normalStart = RestoreFlowPolicy.ClassifyStart(validatedMod, false);
Require(normalStart.CanRestore && normalStart.StartMode == RestoreStartMode.Normal && !normalStart.RecoveryMode, "4 normal mode restore flow");
RestoreDetection bootStart = RestoreFlowPolicy.ClassifyStart(missing, true);
Require(bootStart.CanRestore && bootStart.StartMode == RestoreStartMode.Bootloader && bootStart.RecoveryMode, "5 bootloader restore flow");
Require(!RestoreFlowPolicy.ClassifyStart(validatedMod, true).CanRestore, "ambiguous normal+boot rejected");

FirmwareService confirmationService = new();
try
{
    confirmationService.RestoreStockFirmwareAsync("CANCELLED").GetAwaiter().GetResult();
    throw new Exception("6 cancelled restore accepted");
}
catch (FirmwareUpdateException ex)
{
    Require(!ex.Destructive && ex.Outcome == UpdaterState.Failed, "6 cancel/invalid confirmation must cause zero destructive commands");
}
Require(!StockRecoveryPolicy.IsConfirmed("CANCELLED") && StockRecoveryPolicy.IsConfirmed(StockRecoveryPolicy.Confirmation), "restore confirmation policy");

var bootEntryTimeout = new FirmwareUpdateException("SC3 bootloader did not appear.", false);
Require(!bootEntryTimeout.Destructive && bootEntryTimeout.Outcome == UpdaterState.Failed, "7 bootloader entry timeout is pre-destructive");

// 8 ACK fail-closed is exercised above for malformed, stale and I/O-failed ACKs.
Require(FirmwareOutcomeClassifier.ClassifyRestorePostFailure(true, missing, true) == UpdaterState.RestoreFailedBootloaderAvailable,
    "9 destructive transfer failure with bootloader available");
Require(FirmwareOutcomeClassifier.ClassifyRestorePostFailure(true, validatedMod, false) == UpdaterState.RestoreFailedDeviceHealthy,
    "10 finalization failure with healthy normal device");
Require(FirmwareOutcomeClassifier.ClassifyRestorePostFailure(true, missing, false) == UpdaterState.RestoreRecoveryRequired,
    "11 reboot timeout with neither state");
Require(FirmwareOutcomeClassifier.ClassifyRestorePostFailure(true, validatedStock, false) == UpdaterState.RestoreFailedDeviceHealthy,
    "12 healthy normal after restore failure");
Require(FirmwareOutcomeClassifier.ClassifyRestorePostFailure(true, missing, true) == UpdaterState.RestoreFailedBootloaderAvailable,
    "13 bootloader remaining after restore failure");
Require(StockVerificationPolicy.IsVerifiedStock(validatedStock, false), "14 successful stock verification");
Require(!StockVerificationPolicy.IsVerifiedStock(validatedMod, false), "15 RGB+ attestation must be absent after stock restore");
Require(FirmwarePresentationPolicy.ReadyLabel(validatedStock, false, false) == "RGB setup required", "16 app returns to RGB setup required");
Require(FirmwarePresentationPolicy.ReadyLabel(validatedMod, false, true) != "RGB Ready", "17 RGB Ready hidden during restore");
Require(FirmwarePresentationPolicy.ReadyLabel(validatedMod, false, false) == "RGB Ready", "18 existing Mod 1.4 ready state regression");
Require(MvaPackage.LoadApproved(firmware).Sha256 == ReleasePolicy.FirmwareSha256, "18 existing Mod 1.4 package validation regression");

Console.WriteLine("PASS: Restore Original Firmware tests 1-18 + existing Mod 1.4 updater regression");
