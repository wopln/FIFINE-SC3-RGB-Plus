using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SC3FirmwareTool.Core;

public sealed class FirmwareService
{
    public event Action<UpdateProgress>? ProgressChanged;
    public static string DefaultFirmwarePath => Path.Combine(AppContext.BaseDirectory, "Firmware", ReleasePolicy.FirmwareFileName);

    public DeviceStatus Detect()
    {
        HidIdentity? identity = HidDiscovery.FindValidatedNormal();
        if (identity is null)
        {
            bool anySc3 = HidDiscovery.Enumerate().Any(x => x.Vid == ReleasePolicy.NormalVid && x.Pid == ReleasePolicy.NormalPid);
            return new(false, false, false, anySc3 ? "Unsupported or unvalidated SC3 revision." : "No FIFINE SC3 detected.");
        }
        try
        {
            if (QueryOfficialVersion(identity) != ReleasePolicy.OfficialVersion)
                return new(true, false, false, "Unsupported or unvalidated SC3 firmware revision.", identity.Path);
            bool mod = QueryAttestation(identity);
            return new(true, true, mod, mod ? "SC3 RGB+ Mod 1.4 ready." : "Validated SC3 detected; RGB setup required.", identity.Path, ReleasePolicy.ValidatedHidInstance);
        }
        catch (Exception ex) { return new(true, false, false, "SC3 detected; firmware validation failed: " + ex.Message, identity.Path); }
    }

    public string Info()
    {
        DeviceStatus status = Detect();
        return JsonSerializer.Serialize(new
        {
            releaseTier = ReleasePolicy.NativeUpdaterReleaseTier,
            status.Present, status.ValidatedProfile, status.ModInstalled, status.Message,
            officialVersion = status.Present ? QueryOfficialVersion(HidDiscovery.FindValidatedNormal()!) : null,
            expectedBuildId = ReleasePolicy.BuildId
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public MvaPackage Verify(string? path = null) => MvaPackage.LoadApproved(path ?? DefaultFirmwarePath);

    public DryRunResult DryRun(string? path = null)
    {
        Report(UpdaterState.ValidatingDevice, 0, "Validating device");
        DeviceStatus device = Detect();
        if (!device.ValidatedProfile) throw new FirmwareUpdateException(device.Message);
        Report(UpdaterState.ValidatingFirmware, 0, "Validating firmware");
        MvaPackage package = Verify(path);
        IReadOnlyList<ProtocolStep> plan = ProtocolPlan.Build(package);
        int fullData = plan.Count(x => x.Operation == ProtocolOperation.Write && x.DataBlock > 0);
        int fullAck = plan.Count(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK", StringComparison.Ordinal));
        int constData = ((package.ConstLength + 4095) / 4096) * 16;
        int constAck = (package.ConstLength + 4095) / 4096;
        int data = constData + fullData;
        int ack = constAck + fullAck;
        int outbound = 1 + (constData + 4) + (fullData + 5);
        if (constData != 1808 || constAck != 113 || fullData != 6496 || fullAck != 406 || outbound != 8314)
            throw new FirmwareUpdateException($"Two-stage protocol plan count mismatch ({constData}/{constAck}/{fullData}/{fullAck}/{outbound}).");
        if (plan.Where(x => x.Bytes is not null && x.Operation == ProtocolOperation.Write).Any(x => x.Bytes!.Length != 256))
            throw new FirmwareUpdateException("Protocol report size mismatch.");
        if (!plan.Any(x => x.Bytes?.AsSpan(0, 8).SequenceEqual("chiperas"u8) == true) ||
            !plan.Any(x => x.Bytes?.AsSpan(0, 8).SequenceEqual("codedata"u8) == true) ||
            !plan.Any(x => x.Bytes?.AsSpan(0, 6).SequenceEqual("upinfo"u8) == true))
            throw new FirmwareUpdateException("Required protocol stages missing.");
        byte[] constSelection = (byte[])package.Metadata.Clone();
        Convert.FromHexString("FF55FFFFFF").CopyTo(constSelection, 8);
        byte[] fullSelection = (byte[])package.Metadata.Clone();
        Convert.FromHexString("5555FFFFFF").CopyTo(fullSelection, 8);
        if (ProtocolPlan.ValidateSectionSelection(package.Metadata, constSelection) != FirmwareSectionSelection.ConstOnly ||
            ProtocolPlan.ValidateSectionSelection(package.Metadata, fullSelection) != FirmwareSectionSelection.CodeAndConst)
            throw new FirmwareUpdateException("Two-stage selection validation failed.");
        if (VendorTransferTiming.SectionPrepareDelay(package.CodeLength) != TimeSpan.FromMilliseconds(879) ||
            VendorTransferTiming.SectionPrepareDelay(package.ConstLength) != TimeSpan.FromMilliseconds(339))
            throw new FirmwareUpdateException("Vendor section pacing validation failed.");
        Report(UpdaterState.Success, 100, "Dry run passed", data, data, (long)data * 256, TimeSpan.Zero);
        return new(true, data, ack, outbound, package.Sha256,
            $"Exact-unit profile, approved MVA, vendor erase behavior (16 polls, 500 ms spacing, 6.5 s read allowance, Windows 31/121 no-status only after valid progress, exact metadata selection required, 1000 ms post-erase settle), vendor section pacing (Code 879 ms / Const 339 ms), strict 256-byte sector ACK fail-closed policy, Const-only then Code+Const, {data} data reports, {ack} sector ACKs and both finalizations validated; no bootloader, erase or firmware-write command sent.");
    }

    public async Task InstallRgbAsync(string explicitConfirmation, CancellationToken cancellationToken = default)
    {
        if (explicitConfirmation != ReleasePolicy.BuildId)
            throw new FirmwareUpdateException("Explicit install confirmation missing.");
        string logPath = CreateLogPath(); bool destructive = false;
        void Log(string text) => File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {text}{Environment.NewLine}");
        void Set(UpdaterState state, int percent, string message, int current = 0, int total = 0, long bytes = 0, TimeSpan elapsed = default)
        { Log($"{state} {percent}% {message}"); Report(state, percent, message, current, total, bytes, elapsed); }
        Log($"UpdaterHost=InProcessCore ProcessPath={Environment.ProcessPath ?? "unknown"}");
        Log($"BaseDirectory={AppContext.BaseDirectory} CurrentDirectory={Environment.CurrentDirectory} Architecture={RuntimeInformation.ProcessArchitecture}");
        Log($"FirmwarePath={DefaultFirmwarePath}");
        try
        {
            Set(UpdaterState.ValidatingDevice, 0, "Preparing SC3");
            HidIdentity? normal = HidDiscovery.FindValidatedNormal();
            HidIdentity? boot = HidDiscovery.FindBoot();
            if (normal is null && boot is null) throw new FirmwareUpdateException("Validated SC3 or its recovery bootloader was not found.");
            if (normal is not null && boot is not null) throw new FirmwareUpdateException("Ambiguous SC3 state.");
            Set(UpdaterState.ValidatingFirmware, 0, "Checking approved firmware");
            MvaPackage package = Verify();
            IReadOnlyList<ProtocolStep> plan = ProtocolPlan.Build(package);
            if (plan.Count(x => x.Operation == ProtocolOperation.Write && x.DataBlock > 0) != 6496 ||
                plan.Count(x => x.Operation == ProtocolOperation.Read && x.Label.Contains("ACK", StringComparison.Ordinal)) != 406)
                throw new FirmwareUpdateException("Validated protocol plan mismatch.");
            Log($"FirmwareSha256={package.Sha256} BuildId={ReleasePolicy.BuildId} NormalProfile={ReleasePolicy.NormalVid:X4}:{ReleasePolicy.NormalPid:X4} BootProfile={ReleasePolicy.BootVid:X4}:{ReleasePolicy.BootPid:X4}");
            cancellationToken.ThrowIfCancellationRequested();
            Native.SetThreadExecutionState(0x80000001 | 0x00000040);
            Stopwatch elapsed = Stopwatch.StartNew();
            if (normal is not null) boot = await EnterBootloaderAsync(normal, plan, cancellationToken, false, Set);
            bool fullImageWritten = false;
            for (int session = 1; session <= 2 && !fullImageWritten; session++)
            {
                destructive = true;
                Set(UpdaterState.BootloaderConnected, 3, $"Installing firmware (stage {session}/2)");
                (bool code, bool constants) = await RunBootSessionAsync(boot!, package, elapsed, Log, Set);
                fullImageWritten = code && constants;
                Set(UpdaterState.WaitingForReboot, fullImageWritten ? 99 : 45, "Restarting SC3");
                await WaitUntilAsync(() => HidDiscovery.FindBoot() is null, TimeSpan.FromSeconds(15), CancellationToken.None, true, "Bootloader did not disconnect.");
                if (!fullImageWritten)
                {
                    boot = await WaitForAsync(HidDiscovery.FindBoot, TimeSpan.FromSeconds(8), CancellationToken.None, true, "SC3 did not return to bootloader for the required second stage.");
                    Log("First stage selected Const only; continuing with bounded second vendor-proven stage.");
                }
            }
            if (!fullImageWritten) throw new FirmwareUpdateException("Code and Const were not both selected within two stages.", true);
            HidIdentity returned = await WaitForAsync(HidDiscovery.FindValidatedNormal, TimeSpan.FromSeconds(30), CancellationToken.None, true, "Validated SC3 did not return.");
            Set(UpdaterState.VerifyingDevice, 99, "Verifying SC3");
            if (QueryOfficialVersion(returned) != ReleasePolicy.OfficialVersion || !QueryAttestation(returned))
                throw new FirmwareUpdateException("Post-install firmware identity verification failed.", true);
            Set(UpdaterState.SetupSucceeded, 100, "RGB control ready", 6496, 6496, 6496L * 256, elapsed.Elapsed);
        }
        catch (OperationCanceledException) when (!destructive)
        { Report(UpdaterState.Failed, 0, "Installation cancelled safely."); throw; }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} ERROR {ex}{Environment.NewLine}");
            bool effectiveDestructive = destructive || ex is FirmwareUpdateException { Destructive: true };
            DeviceStatus postFailureNormal;
            bool bootPresent;
            try
            {
                postFailureNormal = Detect();
                bootPresent = HidDiscovery.FindBoot() is not null;

                // USB re-enumeration can lag the failing HID call. Avoid
                // misclassifying a still-reachable setup bootloader as a
                // recovery-required device solely because the first probe won
                // the race against PnP arrival.
                if (effectiveDestructive && !postFailureNormal.Present && !bootPresent)
                {
                    Stopwatch postFailureProbe = Stopwatch.StartNew();
                    while (postFailureProbe.Elapsed < TimeSpan.FromSeconds(8) &&
                           !postFailureNormal.Present && !bootPresent)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        postFailureNormal = Detect();
                        bootPresent = HidDiscovery.FindBoot() is not null;
                    }
                    Log($"PostFailureReenumerationProbe elapsedMs={postFailureProbe.Elapsed.TotalMilliseconds:F0} normalPresent={postFailureNormal.Present} bootPresent={bootPresent}");
                }
            }
            catch (Exception verifyEx)
            {
                Log($"PostFailureVerificationError={verifyEx.GetType().Name}: {verifyEx.Message}");
                postFailureNormal = new(false, false, false, "Post-failure device verification failed.");
                bootPresent = HidDiscovery.FindBoot() is not null;
            }

            UpdaterState outcome = FirmwareOutcomeClassifier.ClassifyPostFailure(effectiveDestructive, postFailureNormal, bootPresent);
            string message = outcome switch
            {
                UpdaterState.SetupSucceeded => "RGB control ready.",
                UpdaterState.SetupFailedDeviceHealthy => "RGB setup failed. SC3 is still working normally.",
                UpdaterState.SetupFailedBootloaderAvailable => "RGB setup did not complete. SC3 is in the known setup bootloader; no automatic retry was attempted.",
                UpdaterState.RecoveryRequired => "SC3 recovery is required.",
                _ => ex.Message
            };
            Log($"PostFailureOutcome={outcome} normalPresent={postFailureNormal.Present} validated={postFailureNormal.ValidatedProfile} modInstalled={postFailureNormal.ModInstalled} bootPresent={bootPresent} destructive={effectiveDestructive}");
            Report(outcome, 0, message);
            if (outcome == UpdaterState.SetupSucceeded) return;
            throw new FirmwareUpdateException(ex.Message, effectiveDestructive, ex, outcome);
        }
        finally { Native.SetThreadExecutionState(0x80000000); }
    }

    private async Task<HidIdentity> EnterBootloaderAsync(HidIdentity normal, IReadOnlyList<ProtocolStep> plan,
        CancellationToken token, bool destructive,
        Action<UpdaterState,int,string,int,int,long,TimeSpan> set)
    {
        set(UpdaterState.EnteringBootloader, 1, "Preparing SC3", 0, 0, 0, TimeSpan.Zero);
        using (HidConnection connection = new(normal))
        {
            connection.SetFeature(plan.First(x => x.Operation == ProtocolOperation.BootFeatureWrite).Bytes!);
            if (!connection.GetFeature().SequenceEqual(plan.First(x => x.Operation == ProtocolOperation.BootFeatureRead).Bytes!))
                throw new FirmwareUpdateException("Bootloader acknowledgement mismatch.", destructive);
        }
        set(UpdaterState.WaitingForBootloader, 2, "Restarting SC3", 0, 0, 0, TimeSpan.Zero);
        await WaitUntilAsync(() => HidDiscovery.FindValidatedNormal() is null, TimeSpan.FromSeconds(15), token, destructive, "Normal SC3 did not disconnect.");
        return await WaitForAsync(HidDiscovery.FindBoot, TimeSpan.FromSeconds(15), token, destructive, "SC3 bootloader did not appear.");
    }

    private async Task<(bool Code, bool Constants)> RunBootSessionAsync(HidIdentity boot, MvaPackage package,
        Stopwatch elapsed, Action<string> log,
        Action<UpdaterState,int,string,int,int,long,TimeSpan> set)
    {
        using HidConnection connection = new(boot);
        set(UpdaterState.Erasing, 4, "Installing firmware", 0, 0, 0, elapsed.Elapsed);
        connection.Write(Pad("chiperas"u8));
        int previousProgress = 0; bool erased = false; int noStatusFailures = 0; int pollsAttempted = 0;
        for (int poll = 0; poll < VendorTransferTiming.ErasePollCount; poll++)
        {
            pollsAttempted = poll + 1;
            byte[] response;
            try
            {
                response = connection.Read(VendorTransferTiming.EraseReadTimeoutMilliseconds);
            }
            catch (FirmwareUpdateException ex) when (ErasePollPolicy.IsVendorNoStatusFailure(previousProgress, ex))
            {
                noStatusFailures++;
                log($"EraseNoStatus poll={poll + 1}/{VendorTransferTiming.ErasePollCount} lastProgress={previousProgress}/30 windowsError={ex.WindowsError} timedOut={ex.TimedOut} action=continue-distinct-poll staleBufferUsed=false");
                if (poll + 1 < VendorTransferTiming.ErasePollCount)
                {
                    log($"ErasePollDelay poll={poll + 1} progress=unknown lastValid={previousProgress}/30 delayMs={VendorTransferTiming.ErasePollMilliseconds} source=vendor-helper-Sleep(500)");
                    await Task.Delay(VendorTransferTiming.ErasePollDelay).ConfigureAwait(false);
                }
                continue;
            }
            if (response.Length != 256 || !response.AsSpan(0, 4).SequenceEqual(BitConverter.GetBytes(0x200000)) ||
                !response.AsSpan(4, 4).SequenceEqual("eras"u8) || response[9] != 30 || response.AsSpan(10).IndexOfAnyExcept((byte)0) >= 0)
                throw new FirmwareUpdateException("Malformed erase status.", true);
            int value = response[8];
            if (value <= previousProgress || value > 30) throw new FirmwareUpdateException("Stale or invalid erase progress.", true);
            previousProgress = value;
            log($"EraseProgress poll={poll + 1}/{VendorTransferTiming.ErasePollCount} progress={value}/30");
            if (value == 30) { erased = true; break; }
            log($"ErasePollDelay poll={poll + 1} progress={value}/30 delayMs={VendorTransferTiming.ErasePollMilliseconds} source=vendor-helper-Sleep(500)");
            await Task.Delay(VendorTransferTiming.ErasePollDelay).ConfigureAwait(false);
        }
        bool deferredEraseValidation = !erased && ErasePollPolicy.AllowsDeferredMetadataValidation(previousProgress, pollsAttempted, noStatusFailures);
        if (!erased && !deferredEraseValidation)
            throw new FirmwareUpdateException("Erase did not positively complete and vendor-compatible deferred validation was not eligible.", true);
        if (deferredEraseValidation)
            log($"EraseCompletionDeferred polls={pollsAttempted}/{VendorTransferTiming.ErasePollCount} lastValidProgress={previousProgress}/30 noStatusFailures={noStatusFailures} fullVendorPollWindow=true requireExactMetadataSelection=true");
        log($"PostEraseSettleDelay delayMs={VendorTransferTiming.PostEraseSettleMilliseconds} source=vendor-helper-Sleep(1000)");
        await Task.Delay(VendorTransferTiming.PostEraseSettleDelay).ConfigureAwait(false);
        set(UpdaterState.PreparingUpdate, 8, "Installing firmware", 0, 0, 0, elapsed.Elapsed);
        connection.Write(package.Metadata);
        byte[] selection = connection.Read();
        FirmwareSectionSelection selected = ProtocolPlan.ValidateSectionSelection(package.Metadata, selection);
        if (deferredEraseValidation)
            log($"EraseCompletionConfirmedByMetadataSelection selection={selected} staleBufferUsed=false");
        bool code = selected == FirmwareSectionSelection.CodeAndConst;
        bool constants = true;
        log($"FirmwareSectionSelection={selected} code={code} constants={constants}");
        int total = (code ? ((package.CodeLength + 4095) / 4096) * 16 : 0) + ((package.ConstLength + 4095) / 4096) * 16;
        int current = 0; byte[] previous = selection;
        if (code) (previous,current) = await TransferSection(connection, package, "codedata"u8.ToArray(), package.CodeStart, package.CodeLength, previous, current, total, elapsed, log);
        if (constants) (previous,current) = await TransferSection(connection, package, "constdat"u8.ToArray(), package.ConstStart, package.ConstLength, previous, current, total, elapsed, log);
        set(UpdaterState.Finalizing, code ? 99 : 44, "Finalizing firmware", current, total, (long)current * 256, elapsed.Elapsed);
        byte[] final = new byte[256]; "upinfo"u8.CopyTo(final); previous.AsSpan(6,2).CopyTo(final.AsSpan(6)); "ok"u8.CopyTo(final.AsSpan(8)); previous.AsSpan(10).CopyTo(final.AsSpan(10));
        log($"Finalize send sectionSelection={selected} reportsWritten={current}/{total}");
        bool finalOk=connection.TryFinalWrite(final,out int error,out TimeSpan finalTime);
        log($"Finalize completion ok={finalOk} windowsError={error} elapsedMs={finalTime.TotalMilliseconds:F3}; never sufficient for success");
        if(!finalOk && (!(error is 31 or 995 or 1167)||finalTime>TimeSpan.FromMilliseconds(250))) throw new FirmwareUpdateException("Unexpected finalization I/O failure.",true);
        return (code,constants);
    }

    private async Task<(byte[] Previous, int Current)> TransferSection(HidConnection connection, MvaPackage package, ReadOnlyMemory<byte> name,
        int start, int length, byte[] previous, int current, int total, Stopwatch elapsed, Action<string> log)
    {
        string sectionName = System.Text.Encoding.ASCII.GetString(name.Span);
        byte[] prepare = new byte[256]; name.Span.CopyTo(prepare); previous.AsSpan(8).CopyTo(prepare.AsSpan(8)); connection.Write(prepare);
        int sectors = VendorTransferTiming.SectorCount(length);
        TimeSpan prepareDelay = VendorTransferTiming.SectionPrepareDelay(length);
        log($"SectionPrepareDelay section={sectionName} sectors={sectors} delayMs={prepareDelay.TotalMilliseconds:F0} source=vendor-helper-Sleep(sectors*3ms)");
        await Task.Delay(prepareDelay).ConfigureAwait(false);
        int ackOrdinal = 0;
        foreach (int sector in Enumerable.Range(1, sectors - 1).Append(0))
        {
            ackOrdinal++;
            byte[] group = new byte[4096]; int offset = start + sector * 4096;
            int available = Math.Max(0, Math.Min(4096, package.Data.Length - offset));
            if (available > 0) package.Data.AsSpan(offset, available).CopyTo(group);
            for (int block = 0; block < 16; block++) { connection.Write(group.AsSpan(block * 256, 256)); current++; }
            byte[] expected = group.AsSpan(0, 256).ToArray(); expected[8] = 0x55;
            string expectedPrefix = Convert.ToHexString(expected.AsSpan(0, 16));
            // The successful vendor capture submits GET_REPORT essentially
            // immediately after the final synchronous SET_REPORT completes
            // (~0.03 ms later). Do the same; there is no extra 3 ms host delay.
            log($"SectorAckPending section={sectionName} ordinal={ackOrdinal}/{sectors} sector={sector} reportsWritten={current}/{total} expectedPrefix={expectedPrefix}");
            byte[] actual;
            try
            {
                actual = connection.Read();
            }
            catch (FirmwareUpdateException ex)
            {
                log($"SectorAckIoFailure section={sectionName} ordinal={ackOrdinal}/{sectors} sector={sector} reportsWritten={current}/{total} expectedPrefix={expectedPrefix} windowsError={ex.WindowsError?.ToString() ?? "none"} timedOut={ex.TimedOut} error={ex.Message}; failClosed=true retry=false finalizeSent=false");
                throw SectorAckPolicy.ReadFailure(sectionName, sector, ackOrdinal, ex);
            }
            try
            {
                SectorAckPolicy.Validate(expected, actual, sectionName, sector, ackOrdinal);
            }
            catch (FirmwareUpdateException)
            {
                log($"SectorAckMismatch section={sectionName} ordinal={ackOrdinal}/{sectors} sector={sector} reportsWritten={current}/{total} expectedPrefix={expectedPrefix} actualLength={actual.Length} actualPrefix={Convert.ToHexString(actual.AsSpan(0, Math.Min(16, actual.Length)))}; failClosed=true retry=false finalizeSent=false");
                throw;
            }
            log($"SectorAckOk section={sectionName} ordinal={ackOrdinal}/{sectors} sector={sector} reportsWritten={current}/{total}");
            previous = expected;
            int percent = 8 + (int)(90L * current / total);
            Report(UpdaterState.Transferring, percent, "Installing firmware", current, total, (long)current * 256, elapsed.Elapsed);
            if ((current & 255) == 0) log($"Transferred {current}/{total} reports");
            await Task.Yield();
        }
        return (previous, current);
    }

    private static string QueryOfficialVersion(HidIdentity identity)
    {
        using HidConnection connection = new(identity); connection.Write(Pad(Convert.FromHexString("A55A000016")));
        byte[] response = connection.Read(); byte[] expected = Pad(Convert.FromHexString("A55A00073100010101210516"));
        if (!response.SequenceEqual(expected)) throw new FirmwareUpdateException("Official version response mismatch.");
        return ReleasePolicy.OfficialVersion;
    }
    private static bool QueryAttestation(HidIdentity identity)
    {
        using HidConnection connection = new(identity); connection.Write(Pad(Convert.FromHexString("A55AFC010216")));
        return connection.Read().AsSpan(0, 8).SequenceEqual(ReleasePolicy.Attestation);
    }
    private static byte[] Pad(ReadOnlySpan<byte> value) { byte[] r = new byte[256]; value.CopyTo(r); return r; }
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token, bool destructive, string failure)
    { var sw=Stopwatch.StartNew(); while(sw.Elapsed<timeout){if(condition())return; token.ThrowIfCancellationRequested(); await Task.Delay(250,token);} throw new FirmwareUpdateException(failure,destructive); }
    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken token, bool destructive, string failure) where T:class
    { var sw=Stopwatch.StartNew(); while(sw.Elapsed<timeout){if(probe() is { } value)return value; token.ThrowIfCancellationRequested(); await Task.Delay(250,token);} throw new FirmwareUpdateException(failure,destructive); }
    private void Report(UpdaterState state, int percent, string message, int current=0, int total=0, long bytes=0, TimeSpan elapsed=default) => ProgressChanged?.Invoke(new(state,percent,message,current,total,bytes,elapsed));
    private static string CreateLogPath(){string d=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FIFINE SC3 RGB+","Logs");Directory.CreateDirectory(d);return Path.Combine(d,$"firmware-{DateTime.Now:yyyyMMdd-HHmmss}.log");}
}
