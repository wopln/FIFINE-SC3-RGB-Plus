using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SC3RGBController.Services;

public sealed class HidDeviceClient : ISc3CustomButtonTransport, IDisposable
{
    public const ushort ExpectedVid = 0x3142;
    public const ushort ExpectedPid = 0x0C33;
    public const ushort ExpectedUsagePage = 0xFF00;
    public const ushort ExpectedUsage = 0x55AA;
    public const int ExpectedInterface = 4;
    public const int ExpectedInputLength = 257;
    public const int ExpectedOutputLength = 257;
    public const int ExpectedFeatureLength = 9;
    public static bool RgbWritesEnabled => true;
    public static string OutputTransport => "HidD_SetOutputReport";

    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    private readonly object _gate = new();
    private SafeFileHandle? _writeHandle;
    private DeviceIdentity? _validatedIdentity;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _writeHandle is { IsInvalid: false, IsClosed: false } &&
                       _validatedIdentity?.MatchesExpected == true;
            }
        }
    }

    public string DeviceSummary => "FIFINE SC3 · VID 3142 · PID 0C33 · IF4 · FF00:55AA";

    public bool Probe(out string detail)
    {
        lock (_gate)
        {
            DeviceIdentity? identity = FindExactDevice();
            if (identity is null)
            {
                detail = "FIFINE SC3 vendor HID not found";
                return false;
            }

            detail = $"Connected · {identity.Product} · Output {identity.OutputReportLength} bytes";
            return true;
        }
    }

    public bool TryOpen(out string detail)
    {
        lock (_gate)
        {
            if (_writeHandle is { IsInvalid: false, IsClosed: false } &&
                _validatedIdentity?.MatchesExpected == true)
            {
                detail = "Connected";
                return true;
            }

            CloseLocked();
            DeviceIdentity? identity = FindExactDevice();
            if (identity is null)
            {
                detail = "FIFINE SC3 vendor HID not found";
                return false;
            }

            SafeFileHandle handle = Native.CreateFile(
                identity.Path,
                GenericReadWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                detail = $"HID open failed · Windows error {Marshal.GetLastWin32Error()}";
                handle.Dispose();
                return false;
            }

            _validatedIdentity = identity;
            _writeHandle = handle;
            detail = "Connected";
            return true;
        }
    }

    public bool TryWriteColor(byte red, byte green, byte blue, out string detail)
    {
        lock (_gate)
        {
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } ||
                _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }

            byte[] report = BuildCustomRgbReport(red, green, blue);
            ValidateCustomRgbReport(report, red, green, blue);

            bool ok = Native.HidD_SetOutputReport(
                _writeHandle,
                report,
                report.Length);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                detail = $"RGB Output Report failed · Windows error {error}";
                CloseLocked();
                return false;
            }

            detail = $"Applied #{red:X2}{green:X2}{blue:X2}";
            return true;
        }
    }

    public bool TryDisableCustomRgb(out string detail)
    {
        lock (_gate)
        {
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } ||
                _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }

            byte[] report = BuildDisableCustomRgbReport();
            bool ok = Native.HidD_SetOutputReport(
                _writeHandle,
                report,
                report.Length);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                detail = $"Restore Output Report failed · Windows error {error}";
                CloseLocked();
                return false;
            }

            detail = $"Returned to stock RGB mode · {OutputTransport}";
            return true;
        }
    }

    public bool TryQuerySc3(out Sc3QueryReply reply, out string detail)
    {
        lock (_gate)
        {
            reply = new(Sc3FirmwareFlavor.Unknown, false, false, 0, 0, 0, 0);
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } || _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }
            byte[] query = BuildFirmwareQueryReport();
            if (!Native.HidD_SetOutputReport(_writeHandle, query, query.Length))
            {
                detail = $"FC/02 query failed · Windows error {Marshal.GetLastWin32Error()}";
                CloseLocked();
                return false;
            }
            byte[] response = new byte[ExpectedInputLength];
            if (!Native.HidD_GetInputReport(_writeHandle, response, response.Length))
            {
                detail = $"FC/02 reply failed · Windows error {Marshal.GetLastWin32Error()}";
                CloseLocked();
                return false;
            }
            if (!Sc3QueryReply.TryParse(response, out reply))
            {
                detail = "FC/02 reply was invalid";
                return false;
            }
            detail = "FC/02 reply valid";
            return true;
        }
    }

    public bool TrySetCustomButtonMode(bool enabled, out string detail)
    {
        lock (_gate)
        {
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } || _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }
            byte[] report = BuildCustomButtonModeReport(enabled);
            if (!Native.HidD_SetOutputReport(_writeHandle, report, report.Length))
            {
                detail = $"Custom Button mode command failed · Windows error {Marshal.GetLastWin32Error()}";
                CloseLocked();
                return false;
            }
            detail = enabled ? "Shortcut mode enabled" : "Shortcut mode off sent";
            return true;
        }
    }
    public void Close()
    {
        lock (_gate)
        {
            CloseLocked();
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void CloseLocked()
    {
        _writeHandle?.Dispose();
        _writeHandle = null;
        _validatedIdentity = null;
    }

    public static byte[] BuildCustomRgbReport(byte red, byte green, byte blue)
    {
        // Windows HID API buffer: byte 0 Report ID + 256-byte HID payload.
        // SC3 V22 RGB Mod 1.2 command. Physical LED channel order is R,B,G,
        // so keep the UI/API standard RGB and swap G/B only at transport time:
        // A5 5A FC 04 01 R B G 16 + zero padding.
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x04, 0x01, red, blue, green, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }

    public static byte[] BuildDisableCustomRgbReport()
    {
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x01, 0x00, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }

    public static byte[] BuildFirmwareQueryReport() => BuildWhitelistedFc01Report(0x02);
    public static byte[] BuildCustomButtonModeReport(bool enabled) => BuildWhitelistedFc01Report(enabled ? (byte)0x03 : (byte)0x04);

    private static byte[] BuildWhitelistedFc01Report(byte subcommand)
    {
        if (subcommand is not (0x02 or 0x03 or 0x04)) throw new InvalidOperationException("FC/01 whitelist rejected subcommand");
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x01, subcommand, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }
    public static void ValidateCustomRgbReport(byte[] report, byte red, byte green, byte blue)
    {
        if (report.Length != ExpectedOutputLength || report[0] != 0)
        {
            throw new InvalidOperationException("Invalid HID report length or Report ID");
        }

        byte[] payload = [0xA5, 0x5A, 0xFC, 0x04, 0x01, red, blue, green, 0x16];
        for (int index = 0; index < payload.Length; index++)
        {
            if (report[index + 1] != payload[index])
            {
                throw new InvalidOperationException("Custom RGB payload validation failed");
            }
        }

        if (report.Skip(1 + payload.Length).Any(value => value != 0))
        {
            throw new InvalidOperationException("Custom RGB report padding validation failed");
        }
    }

    public static bool MatchesExpectedIdentity(
        string path,
        ushort vid,
        ushort pid,
        string manufacturer,
        string product,
        string serial,
        ushort usagePage,
        ushort usage,
        ushort inputReportLength,
        ushort outputReportLength,
        ushort featureReportLength)
    {
        return vid == ExpectedVid &&
               pid == ExpectedPid &&
               path.Contains("vid_3142&pid_0c33&mi_04", StringComparison.OrdinalIgnoreCase) &&
               manufacturer.Equals("MV-SILICON", StringComparison.OrdinalIgnoreCase) &&
               product.Equals("fifine SC3", StringComparison.OrdinalIgnoreCase) &&
               serial.Equals("20190808", StringComparison.OrdinalIgnoreCase) &&
               usagePage == ExpectedUsagePage &&
               usage == ExpectedUsage &&
               inputReportLength == ExpectedInputLength &&
               outputReportLength == ExpectedOutputLength &&
               featureReportLength == ExpectedFeatureLength;
    }

    private static DeviceIdentity? FindExactDevice()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfo = Native.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfo == new IntPtr(-1))
        {
            return null;
        }

        List<DeviceIdentity> matches = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDeviceInterfaceData interfaceData = new()
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(
                        deviceInfo, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) // ERROR_NO_MORE_ITEMS
                    {
                        break;
                    }

                    continue;
                }

                Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfo, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfo, ref interfaceData, detail, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.Contains("vid_3142&pid_0c33&mi_04", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DeviceIdentity? identity = InspectDevice(path);
                    if (identity?.MatchesExpected == true)
                    {
                        matches.Add(identity);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceInfo);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static DeviceIdentity? InspectDevice(string path)
    {
        using SafeFileHandle handle = Native.CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        HiddAttributes attributes = new() { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes) ||
            !Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            return null;
        }

        HidpCaps caps;
        try
        {
            if (Native.HidP_GetCaps(preparsed, out caps) < 0)
            {
                return null;
            }
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }

        string manufacturer = GetHidString(Native.HidD_GetManufacturerString, handle);
        string product = GetHidString(Native.HidD_GetProductString, handle);
        string serial = GetHidString(Native.HidD_GetSerialNumberString, handle);
        return new DeviceIdentity(
            path,
            attributes.VendorId,
            attributes.ProductId,
            manufacturer,
            product,
            serial,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength,
            caps.FeatureReportByteLength);
    }

    private delegate bool HidStringGetter(SafeFileHandle handle, StringBuilder buffer, int length);

    private static string GetHidString(HidStringGetter getter, SafeFileHandle handle)
    {
        StringBuilder buffer = new(256);
        return getter(handle, buffer, buffer.Capacity * 2) ? buffer.ToString() : string.Empty;
    }

    private sealed record DeviceIdentity(
        string Path,
        ushort Vid,
        ushort Pid,
        string Manufacturer,
        string Product,
        string Serial,
        ushort UsagePage,
        ushort Usage,
        ushort InputReportLength,
        ushort OutputReportLength,
        ushort FeatureReportLength)
    {
        public bool MatchesExpected => MatchesExpectedIdentity(
            Path,
            Vid,
            Pid,
            Manufacturer,
            Product,
            Serial,
            UsagePage,
            Usage,
            InputReportLength,
            OutputReportLength,
            FeatureReportLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    private static class Native
    {
        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_SetOutputReport(
            SafeFileHandle handle,
            byte[] reportBuffer,
            int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);
        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetManufacturerString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetProductString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetSerialNumberString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData interfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData,
            int detailDataSize,
            out int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}
