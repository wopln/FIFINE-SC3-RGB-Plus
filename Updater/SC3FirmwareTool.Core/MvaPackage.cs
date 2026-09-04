using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SC3FirmwareTool.Core;

public sealed class MvaPackage
{
    public byte[] Data { get; }
    public int CodeStart { get; }
    public int ConstStart { get; }
    public int CodeLength { get; }
    public int ConstLength { get; }
    public byte[] Metadata { get; }
    public string Sha256 { get; }

    private MvaPackage(byte[] data, int codeStart, int constStart, int codeLength,
        int constLength, byte[] metadata, string sha256)
    {
        Data = data; CodeStart = codeStart; ConstStart = constStart;
        CodeLength = codeLength; ConstLength = constLength;
        Metadata = metadata; Sha256 = sha256;
    }

    public static MvaPackage LoadApproved(string path) =>
        LoadExpected(path, ReleasePolicy.FirmwareFileName, ReleasePolicy.FirmwareSha256, ReleasePolicy.FirmwareSize, "RGB+ Mod 1.4");

    public static MvaPackage LoadStockRecovery(string path) =>
        LoadExpected(path, StockRecoveryPolicy.FirmwareFileName, StockRecoveryPolicy.FirmwareSha256, StockRecoveryPolicy.FirmwareSize, "Stock V22 recovery");

    private static MvaPackage LoadExpected(string path, string expectedFileName, string expectedSha256, long expectedSize, string packageLabel)
    {
        if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.Ordinal))
            throw new FirmwareUpdateException($"{packageLabel} firmware filename mismatch.");
        byte[] data = File.ReadAllBytes(path);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (data.LongLength != expectedSize || sha != expectedSha256)
            throw new FirmwareUpdateException($"{packageLabel} firmware size or SHA-256 mismatch.");
        if (!data.AsSpan(0, 5).SequenceEqual(Convert.FromHexString("4D56B15804")))
            throw new FirmwareUpdateException("MVA target/header mismatch.");
        ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - 4, 2));
        if (storedCrc != Crc16(data.AsSpan(0, data.Length - 4)) || data[^2] != 0 || data[^1] != 0)
            throw new FirmwareUpdateException("MVA container CRC mismatch.");

        int offset = 5;
        Dictionary<byte, (int Body, int Size)> records = [];
        foreach (byte kind in new byte[] { 1, 3, 2, 4 })
        {
            if (offset + 5 > data.Length - 4 || data[offset] != kind)
                throw new FirmwareUpdateException("MVA record order mismatch.");
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 1, 4)));
            if (size < 0 || offset + 5L + size > data.Length - 4)
                throw new FirmwareUpdateException("MVA record bounds invalid.");
            records[kind] = (offset + 9, size);
            offset += 5 + size;
        }
        if (offset != data.Length - 4) throw new FirmwareUpdateException("Unexpected MVA trailing bytes.");
        (int code, int codeSize) = records[2];
        (int constants, int constSize) = records[4];
        if ((code, codeSize, constants, constSize) != (1551, 1262620, 1264176, 462645))
            throw new FirmwareUpdateException("Approved MVA layout mismatch.");
        if (U32(data, code - 4) != 0 || U32(data, constants - 4) != 0x135000 ||
            U32(data, code + 0xb0) != 0x135000 || U32(data, code + 0xd8) != 0x1c8000 ||
            (U32(data, code + 0xe0) & 0xffffff) != 0xf0e0)
            throw new FirmwareUpdateException("MVA target profile mismatch.");

        byte[] metadata = new byte[256];
        "cxxx"u8.CopyTo(metadata);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(8), (uint)(codeSize - 4 - 0x10000));
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(12), (uint)(constSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(16), U32(data, code + 0xb0));
        metadata[20] = data[code + 0xff];
        data.AsSpan(code + 0x100b8, 4).CopyTo(metadata.AsSpan(24));
        metadata[28] = (byte)'y';
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(32), U32(data, code + 0xe0) & 0xffffff);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(36), U32(data, code + 0xbc));
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(44), U32(data, code + 0xd8));
        data.AsSpan(0x106db, 4).CopyTo(metadata.AsSpan(48));
        metadata[52] = 2; metadata[53] = 4;
        return new MvaPackage(data, code + 0x10000, constants,
            0x124418, 0x70f31, metadata, sha);
    }

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
