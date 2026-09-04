namespace SC3FirmwareTool.Core;

public enum ProtocolOperation { BootFeatureWrite, BootFeatureRead, BootDisconnect, BootAppear, Write, Read, NormalReturn, Verify }
public enum FirmwareSectionSelection { ConstOnly, CodeAndConst }

public sealed record ProtocolStep(UpdaterState State, ProtocolOperation Operation,
    byte[]? Bytes, string Label, bool Destructive, int DataBlock = 0, int TotalDataBlocks = 0);

public static class ProtocolPlan
{
    public static FirmwareSectionSelection ValidateSectionSelection(byte[] metadata, byte[] response)
    {
        if (metadata.Length != 256 || response.Length != 256)
            throw new FirmwareUpdateException("Firmware section response length mismatch.", true);

        byte[] flags = response.AsSpan(8, 5).ToArray();
        FirmwareSectionSelection selection = flags.SequenceEqual(Convert.FromHexString("FF55FFFFFF"))
            ? FirmwareSectionSelection.ConstOnly
            : flags.SequenceEqual(Convert.FromHexString("5555FFFFFF"))
                ? FirmwareSectionSelection.CodeAndConst
                : throw new FirmwareUpdateException("Unsupported firmware section selection: " + Convert.ToHexString(flags), true);

        byte[] expected = (byte[])metadata.Clone();
        flags.CopyTo(expected, 8);
        if (!response.SequenceEqual(expected))
            throw new FirmwareUpdateException("Firmware section metadata response mismatch.", true);
        return selection;
    }

    public static IReadOnlyList<ProtocolStep> Build(MvaPackage package)
    {
        List<ProtocolStep> steps = [];
        byte[] suffix = Convert.FromHexString("9A75C0");
        byte[] boot = new byte[8]; boot[0] = 0xaa;
        package.Metadata.AsSpan(24, 4).CopyTo(boot.AsSpan(1)); suffix.CopyTo(boot.AsSpan(5));
        steps.Add(new(UpdaterState.EnteringBootloader, ProtocolOperation.BootFeatureWrite, boot, "Enter bootloader", false));
        steps.Add(new(UpdaterState.EnteringBootloader, ProtocolOperation.BootFeatureRead, Convert.FromHexString("5501000304000800"), "Bootloader acknowledgement", false));
        steps.Add(new(UpdaterState.WaitingForBootloader, ProtocolOperation.BootDisconnect, null, "Normal device disconnected", false));
        steps.Add(new(UpdaterState.WaitingForBootloader, ProtocolOperation.BootAppear, null, "Bootloader connected", false));
        steps.Add(new(UpdaterState.Erasing, ProtocolOperation.Write, Pad("chiperas"u8), "Erase application regions", true));
        steps.Add(new(UpdaterState.Erasing, ProtocolOperation.Read, null, "Erase progress", true));
        steps.Add(new(UpdaterState.PreparingUpdate, ProtocolOperation.Write, package.Metadata, "Update metadata", true));
        byte[] selection = (byte[])package.Metadata.Clone();
        selection.AsSpan(8, 5).CopyFrom(Convert.FromHexString("5555FFFFFF"));
        steps.Add(new(UpdaterState.PreparingUpdate, ProtocolOperation.Read, selection, "Code and Const selection", true));

        int totalBlocks = SectorCount(package.CodeLength) * 16 + SectorCount(package.ConstLength) * 16;
        int current = 0; byte[] previous = selection;
        AddSection(steps, package, "codedata"u8, package.CodeStart, package.CodeLength, ref previous, ref current, totalBlocks);
        AddSection(steps, package, "constdat"u8, package.ConstStart, package.ConstLength, ref previous, ref current, totalBlocks);
        byte[] final = new byte[256]; "upinfo"u8.CopyTo(final);
        previous.AsSpan(6, 2).CopyTo(final.AsSpan(6)); "ok"u8.CopyTo(final.AsSpan(8)); previous.AsSpan(10).CopyTo(final.AsSpan(10));
        steps.Add(new(UpdaterState.Finalizing, ProtocolOperation.Write, final, "Finalize", true));
        steps.Add(new(UpdaterState.WaitingForReboot, ProtocolOperation.BootDisconnect, null, "Bootloader disconnected", true));
        steps.Add(new(UpdaterState.VerifyingDevice, ProtocolOperation.NormalReturn, null, "Normal device returned", true));
        steps.Add(new(UpdaterState.VerifyingDevice, ProtocolOperation.Verify, ReleasePolicy.Attestation, "Verify Mod1.4", true));
        return steps;
    }

    private static void AddSection(List<ProtocolStep> steps, MvaPackage package, ReadOnlySpan<byte> name,
        int start, int length, ref byte[] previous, ref int current, int total)
    {
        byte[] prepare = new byte[256]; name.CopyTo(prepare); previous.AsSpan(8).CopyTo(prepare.AsSpan(8));
        steps.Add(new(UpdaterState.PreparingUpdate, ProtocolOperation.Write, prepare, System.Text.Encoding.ASCII.GetString(name), true));
        int sectors = SectorCount(length);
        IEnumerable<int> order = Enumerable.Range(1, sectors - 1).Append(0);
        foreach (int sector in order)
        {
            byte[] group = new byte[4096];
            int offset = start + sector * 4096;
            int available = Math.Max(0, Math.Min(4096, package.Data.Length - offset));
            if (available > 0) package.Data.AsSpan(offset, available).CopyTo(group);
            for (int block = 0; block < 16; block++)
            {
                current++;
                steps.Add(new(UpdaterState.Transferring, ProtocolOperation.Write,
                    group.AsSpan(block * 256, 256).ToArray(), $"sector {sector} block {block}", true, current, total));
            }
            previous = group.AsSpan(0, 256).ToArray(); previous[8] = 0x55;
            steps.Add(new(UpdaterState.Transferring, ProtocolOperation.Read, previous,
                $"sector {sector} ACK", true, current, total));
        }
    }

    private static int SectorCount(int length) => (length + 4095) / 4096;
    private static byte[] Pad(ReadOnlySpan<byte> value) { byte[] result = new byte[256]; value.CopyTo(result); return result; }
    private static void CopyFrom(this Span<byte> target, ReadOnlySpan<byte> source) => source.CopyTo(target);
}
