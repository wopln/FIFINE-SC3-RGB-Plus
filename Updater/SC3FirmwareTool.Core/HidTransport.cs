using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SC3FirmwareTool.Core;

internal sealed record HidIdentity(string Path, ushort Vid, ushort Pid, ushort Version,
    string Manufacturer, string Product, string Serial, ushort UsagePage, ushort Usage,
    ushort InputLength, ushort OutputLength, ushort FeatureLength);

internal static class HidDiscovery
{
    public static IReadOnlyList<HidIdentity> Enumerate()
    {
        Native.HidD_GetHidGuid(out Guid guid);
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return [];
        List<HidIdentity> result = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                InterfaceData item = new() { Size = Marshal.SizeOf<InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    continue;
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref item, detail, needed, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrEmpty(path) && Inspect(path) is { } identity) result.Add(identity);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static HidIdentity? FindValidatedNormal()
    {
        var matches = Enumerate().Where(IsValidatedNormal).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static HidIdentity? FindBoot()
    {
        var matches = Enumerate().Where(x => x.Vid == ReleasePolicy.BootVid && x.Pid == ReleasePolicy.BootPid &&
            x.Path.Contains("vid_0000&pid_2244", StringComparison.OrdinalIgnoreCase) &&
            x.Path.Contains(ReleasePolicy.ValidatedBootHidInstance, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool IsValidatedNormal(HidIdentity x) =>
        x.Vid == ReleasePolicy.NormalVid && x.Pid == ReleasePolicy.NormalPid &&
        x.Version == ReleasePolicy.BcdDevice && x.Manufacturer == ReleasePolicy.Manufacturer &&
        x.Product == ReleasePolicy.Product && x.Serial == ReleasePolicy.Serial &&
        x.UsagePage == ReleasePolicy.UsagePage && x.Usage == ReleasePolicy.Usage &&
        x.InputLength == 257 && x.OutputLength == 257 && x.FeatureLength == 9 &&
        x.Path.Contains("vid_3142&pid_0c33&mi_04", StringComparison.OrdinalIgnoreCase) &&
        x.Path.Contains(ReleasePolicy.ValidatedHidInstance, StringComparison.OrdinalIgnoreCase);

    private static HidIdentity? Inspect(string path)
    {
        using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        Attributes attributes = new() { Size = Marshal.SizeOf<Attributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes) || !Native.HidD_GetPreparsedData(handle, out IntPtr prep)) return null;
        Caps caps;
        try { if (Native.HidP_GetCaps(prep, out caps) < 0) return null; }
        finally { Native.HidD_FreePreparsedData(prep); }
        return new(path, attributes.Vid, attributes.Pid, attributes.Version,
            GetString(Native.HidD_GetManufacturerString, handle), GetString(Native.HidD_GetProductString, handle),
            GetString(Native.HidD_GetSerialNumberString, handle), caps.UsagePage, caps.Usage,
            caps.Input, caps.Output, caps.Feature);
    }

    private delegate bool StringGetter(SafeFileHandle h, StringBuilder b, int n);
    private static string GetString(StringGetter getter, SafeFileHandle handle)
    { StringBuilder b = new(256); return getter(handle, b, 512) ? b.ToString() : string.Empty; }

    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort Links, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues,
            OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
}

internal sealed class HidConnection : IDisposable
{
    private const uint FileFlagOverlapped = 0x40000000;
    private readonly SafeFileHandle _handle;
    public HidIdentity Identity { get; }
    public HidConnection(HidIdentity identity)
    {
        Identity = identity;
        // Match the official upgrade_flash.exe handle exactly. Its CreateFileW
        // call uses GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE,
        // OPEN_EXISTING and FILE_FLAG_OVERLAPPED. The previous native updater
        // omitted FILE_FLAG_OVERLAPPED; under sustained sector traffic that
        // diverged from the vendor path and could end in GET_REPORT Windows 31.
        _handle = Native.CreateFile(identity.Path, 0xC0000000, 3, IntPtr.Zero, 3, FileFlagOverlapped, IntPtr.Zero);
        if (_handle.IsInvalid) throw new FirmwareUpdateException($"Unable to open HID device (Windows {Marshal.GetLastWin32Error()}).");
    }

    public void SetFeature(ReadOnlySpan<byte> payload)
    {
        byte[] report = WithId(payload, 9);
        Invoke(() => Native.HidD_SetFeature(_handle, report, report.Length), "SET_FEATURE", 2000);
    }

    public byte[] GetFeature() => Read(() =>
    {
        byte[] report = new byte[9]; return (Native.HidD_GetFeature(_handle, report, report.Length), report);
    }, "GET_FEATURE", 2000).AsSpan(1).ToArray();

    public void Write(ReadOnlySpan<byte> payload, int timeout = 2000)
    {
        if (payload.Length != 256) throw new FirmwareUpdateException("Boot report must be 256 bytes.", true);
        byte[] report = WithId(payload, 257);
        Invoke(() => Native.HidD_SetOutputReport(_handle, report, report.Length), "SET_REPORT", timeout);
    }

    public bool TryFinalWrite(ReadOnlySpan<byte> payload, out int error, out TimeSpan elapsed)
    {
        byte[] report = WithId(payload, 257); var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = Native.HidD_SetOutputReport(_handle, report, report.Length); error = ok ? 0 : Marshal.GetLastWin32Error();
        sw.Stop(); elapsed = sw.Elapsed; return ok;
    }

    public byte[] Read(int timeout = 2000) => Read(() =>
    {
        byte[] report = new byte[257]; bool ok = Native.HidD_GetInputReport(_handle, report, report.Length);
        return (ok, report);
    }, "GET_REPORT", timeout).AsSpan(1).ToArray();

    private void Invoke(Func<bool> call, string operation, int timeout)
    {
        int error = 0; Task<bool> task = Task.Run(() => { bool ok = call(); error = ok ? 0 : Marshal.GetLastWin32Error(); return ok; });
        if (!task.Wait(timeout)) { Native.CancelIoEx(_handle, IntPtr.Zero); throw new FirmwareUpdateException($"{operation} timed out.", true, timedOut: true); }
        if (!task.Result) throw new FirmwareUpdateException($"{operation} failed (Windows {error}).", true, windowsError: error);
    }

    private T Read<T>(Func<(bool Ok, T Value)> call, string operation, int timeout)
    {
        int error = 0; Task<(bool Ok, T Value)> task = Task.Run(() => { var r = call(); error = r.Ok ? 0 : Marshal.GetLastWin32Error(); return r; });
        if (!task.Wait(timeout)) { Native.CancelIoEx(_handle, IntPtr.Zero); throw new FirmwareUpdateException($"{operation} timed out.", true, timedOut: true); }
        if (!task.Result.Ok) throw new FirmwareUpdateException($"{operation} failed (Windows {error}).", true, windowsError: error);
        return task.Result.Value;
    }

    private static byte[] WithId(ReadOnlySpan<byte> payload, int size)
    { if (payload.Length + 1 > size) throw new ArgumentOutOfRangeException(nameof(payload)); byte[] result = new byte[size]; payload.CopyTo(result.AsSpan(1)); return result; }
    public void Dispose() => _handle.Dispose();
}

internal static class Native
{
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetAttributes(SafeFileHandle h, ref HidDiscovery.Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr p, out HidDiscovery.Caps caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle h, StringBuilder b, int n);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder b, int n);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle h, StringBuilder b, int n);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int n);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int n);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] b, int n);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] b, int n);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid g, string? e, IntPtr p, uint f);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref HidDiscovery.InterfaceData x);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref HidDiscovery.InterfaceData i, IntPtr detail, int size, out int needed, IntPtr d);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateFile(string p, uint a, uint sh, IntPtr sec, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelIoEx(SafeFileHandle h, IntPtr o);
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
}
