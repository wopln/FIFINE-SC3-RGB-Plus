using System.Runtime.InteropServices;

namespace SC3RGBController.Services.Audio;

public enum MediaKey { PlayPause, Next, Previous }

public interface IMediaKeySender
{
    /// <summary>Sends a media key press (down + up). Returns false if Windows rejected the input.</summary>
    bool Send(MediaKey key);
}

/// <summary>Synthesizes global media keys with SendInput so the active media session (Spotify,
/// browser, etc.) responds exactly as it would to a keyboard media key.</summary>
public sealed class MediaKeySender : IMediaKeySender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    public const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static ushort VirtualKeyFor(MediaKey key) => key switch
    {
        MediaKey.PlayPause => VK_MEDIA_PLAY_PAUSE,
        MediaKey.Next => VK_MEDIA_NEXT_TRACK,
        MediaKey.Previous => VK_MEDIA_PREV_TRACK,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    public bool Send(MediaKey key)
    {
        ushort vk = VirtualKeyFor(key);
        INPUT[] inputs =
        [
            Keyboard(vk, KEYEVENTF_EXTENDEDKEY),
            Keyboard(vk, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP),
        ];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static INPUT Keyboard(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } }
    };

    /// <summary>Native sizeof(INPUT) as marshaled (40 on x64, 28 on x86).</summary>
    public static int NativeInputSize => Marshal.SizeOf<INPUT>();

    // INPUT is { DWORD type; union { MOUSEINPUT; KEYBDINPUT; HARDWAREINPUT } }. The union must be
    // as large as MOUSEINPUT (32 bytes on x64) and 8-byte aligned, giving sizeof(INPUT) == 40 on x64
    // and 28 on x86; SendInput fails if cbSize does not match.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
}
