using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SC3RGBController.Services.Apps;

/// <summary>An entry from the Start menu "All apps" list (shell:AppsFolder).</summary>
/// <param name="Name">Friendly name shown in Start, e.g. "Spotify".</param>
/// <param name="ParsingName">AppUserModelID / parsing name; launch with <c>shell:AppsFolder\{ParsingName}</c>.</param>
/// <param name="ProcessName">Probable process name without extension (for per-app audio), or null when unknown (e.g. Store apps).</param>
public sealed record InstalledApp(string Name, string ParsingName, string? ProcessName)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates installed apps (desktop and Store) from the Shell's AppsFolder. All Shell COM work runs on one
/// dedicated background STA thread, so callers on the UI thread never block. Results are cached for the app session.
/// </summary>
public sealed partial class InstalledAppCatalog
{
    public const string AppsFolderPath = "shell:AppsFolder";

    public static InstalledAppCatalog Shared { get; } = new();

    private readonly StaWorker _worker = new("SC3 installed app catalog");
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Task<IReadOnlyList<InstalledApp>>? _apps;

    /// <summary>Installed apps sorted by name. The first call enumerates; later calls return the cached list.</summary>
    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync()
    {
        lock (_gate)
        {
            if (_apps is null || _apps.IsFaulted || _apps.IsCanceled) _apps = _worker.Run(EnumerateApps);
            return _apps;
        }
    }

    /// <summary>Drops the cached app list (e.g. after the user installs something) so the next call re-enumerates.</summary>
    public void Invalidate()
    {
        lock (_gate) _apps = null;
    }

    /// <summary>The app's Start menu icon, or null. Never throws; cached per app and size.</summary>
    public Task<ImageSource?> GetIconAsync(InstalledApp app, int size = 32) =>
        _icons.GetOrAdd($"app|{size}|{app.ParsingName}", _ => _worker.Run(() => LoadIcon(AppsFolderPath + "\\" + app.ParsingName, size)));

    /// <summary>The Shell icon for a file (.exe, .lnk, …), or null. Never throws; cached per path and size.</summary>
    public Task<ImageSource?> GetFileIconAsync(string path, int size = 32) =>
        _icons.GetOrAdd($"file|{size}|{path}", _ => _worker.Run(() => LoadIcon(path, size)));

    /// <summary>Process name (no extension) a program file or shortcut starts, e.g. "Discord" for a Discord .lnk. Null if unknown.</summary>
    public Task<string?> ResolveProcessNameAsync(string fileOrShortcutPath) => _worker.Run(() =>
    {
        try
        {
            string extension = Path.GetExtension(fileOrShortcutPath);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(fileOrShortcutPath);
            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            if (!TryCreateItem(fileOrShortcutPath, out IShellItem? item)) return null;
            try { return ProcessNameFromLink(item as IShellItem2); }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    });

    // ---------- Enumeration (runs on the STA worker) ----------

    private static IReadOnlyList<InstalledApp> EnumerateApps()
    {
        if (!TryCreateItem(AppsFolderPath, out IShellItem? folder)) return [];
        List<InstalledApp> apps = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            Guid bhid = BHID_EnumItems, iid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out object? enumObject) != 0 || enumObject is not IEnumShellItems items)
                return [];
            try
            {
                while (items.Next(1, out IShellItem? child, out uint fetched) == 0 && fetched == 1 && child is not null)
                {
                    try
                    {
                        InstalledApp? app = ReadApp(child);
                        if (app is not null && seen.Add(app.ParsingName)) apps.Add(app);
                    }
                    catch
                    {
                        // A single broken entry must not hide the rest of the list.
                    }
                    finally { Marshal.ReleaseComObject(child); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }

        apps.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return apps;
    }

    private static InstalledApp? ReadApp(IShellItem item)
    {
        string? name = GetDisplayName(item, SIGDN.NORMALDISPLAY)?.Trim();
        string? parsing = GetDisplayName(item, SIGDN.PARENTRELATIVEPARSING);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) return null;

        IShellItem2? item2 = item as IShellItem2;
        string? linkTarget = GetString(item2, PKEY_Link_TargetParsingPath);
        string? resolvedParsing = ResolveKnownFolderPath(parsing);

        if (IsNoise(name, parsing, resolvedParsing, linkTarget)) return null;

        string? processName = ProcessNameFromLink(item2) ?? ProcessNameFromExePath(resolvedParsing) ?? ProcessNameFromPackage(item2, parsing, name);
        return new InstalledApp(name, parsing, processName);
    }

    private static string? ProcessNameFromLink(IShellItem2? item)
    {
        string? target = GetString(item, PKEY_Link_TargetParsingPath);
        if (string.IsNullOrWhiteSpace(target)) return null;
        target = ResolveKnownFolderPath(target) ?? target;
        string? name = ProcessNameFromExePath(target);
        // Squirrel-installed apps (Discord, Slack, Teams classic…) start via "Update.exe --processStart App.exe".
        if (name is not null && name.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            string? arguments = GetString(item, PKEY_Link_Arguments);
            Match match = arguments is null ? Match.Empty : ProcessStartRegex().Match(arguments);
            if (match.Success) return Path.GetFileNameWithoutExtension(match.Groups[1].Value.Trim('"'));
        }
        return name;
    }

    /// <summary>Store/MSIX apps: read Executable="…" for this app's Id from the package's AppxManifest.xml.</summary>
    private static string? ProcessNameFromPackage(IShellItem2? item, string appUserModelId, string displayName)
    {
        int bang = appUserModelId.IndexOf('!');
        if (bang <= 0) return null;
        string? installPath = GetString(item, PKEY_AppUserModel_PackageInstallPath);
        if (string.IsNullOrWhiteSpace(installPath)) return null;
        try
        {
            string manifestPath = Path.Combine(installPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) return null;
            string applicationId = appUserModelId[(bang + 1)..];
            System.Xml.Linq.XDocument manifest = System.Xml.Linq.XDocument.Load(manifestPath);
            foreach (System.Xml.Linq.XElement application in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
            {
                if (!string.Equals((string?)application.Attribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase)) continue;
                string? executable = (string?)application.Attribute("Executable");
                if (executable is null || executable.Contains('$')) return null;
                // Some packages start through a stub (Spotify: SpotifyMigrator.exe -> Spotify.exe). Prefer an exe named
                // after the app when the package ships one next to the entry point.
                string named = Path.Combine(installPath, Path.GetDirectoryName(executable) ?? string.Empty, displayName.Replace(" ", string.Empty) + ".exe");
                return File.Exists(named) ? Path.GetFileNameWithoutExtension(named) : ProcessNameFromExePath(executable);
            }
        }
        catch
        {
            // Unreadable manifest: leave the process name unknown.
        }
        return null;
    }

    private static string? ProcessNameFromExePath(string? path) =>
        path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(path) : null;

    private static readonly string[] NoiseExtensions = [".url", ".txt", ".chm", ".pdf", ".htm", ".html", ".rtf", ".ini", ".log", ".md", ".hlp", ".msi"];

    private static bool IsNoise(string name, string parsing, string? resolvedParsing, string? linkTarget)
    {
        if (parsing.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || parsing.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (string? path in new[] { parsing, resolvedParsing, linkTarget })
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (path.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return true;
            string ext = Path.GetExtension(path);
            if (ext.Length == 0 && Path.IsPathFullyQualified(path) && Directory.Exists(path)) return true; // folders such as "SDK\Samples"
            if (NoiseExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) return true;
            string file = Path.GetFileNameWithoutExtension(path);
            if (UninstallerFileRegex().IsMatch(file)) return true;
        }
        return NoiseNameRegex().IsMatch(name);
    }

    [GeneratedRegex(@"--processStart(?:=|\s+)(""[^""]+""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStartRegex();

    [GeneratedRegex(@"^(unins\d*|uninst(all)?(er)?|setup-uninstall)$", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerFileRegex();

    [GeneratedRegex(@"(\buninstall|\bread ?me\b|\b(help|documentation|manual|release notes|license|website|web site|support|faq)$)", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseNameRegex();

    [GeneratedRegex(@"^\{([0-9A-Fa-f\-]{36})\}(\\.*)?$")]
    private static partial Regex KnownFolderPathRegex();

    /// <summary>Expands "{KNOWNFOLDERID}\rest\of\path" to a full path; returns rooted paths unchanged; null otherwise.</summary>
    private static string? ResolveKnownFolderPath(string path)
    {
        Match match = KnownFolderPathRegex().Match(path);
        if (!match.Success) return Path.IsPathFullyQualified(path) ? path : null;
        if (!Guid.TryParse(match.Groups[1].Value, out Guid folderId)) return null;
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr buffer) != 0) return null;
        try
        {
            string? root = Marshal.PtrToStringUni(buffer);
            return root is null ? null : root + match.Groups[2].Value;
        }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    // ---------- Icons ----------

    private static ImageSource? LoadIcon(string parsingName, int size)
    {
        try
        {
            if (!TryCreateItem(parsingName, out IShellItem? item)) return null;
            try
            {
                if (item is not IShellItemImageFactory factory) return null;
                if (factory.GetImage(new NativeSize(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
                try { return BitmapFromHBitmap(bitmap); }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    /// <summary>Copies a 32bpp HBITMAP into a frozen BitmapSource, keeping its (premultiplied) alpha channel.
    /// Imaging.CreateBitmapSourceFromHBitmap drops alpha for shell icons and paints them on black.</summary>
    private static BitmapSource? BitmapFromHBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0 || info.Width <= 0 || info.Height == 0) return null;
        int width = info.Width, height = Math.Abs(info.Height);
        BitmapInfoHeader header = new()
        {
            Size = 40, // sizeof(BITMAPINFOHEADER), excluding the padding field
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
            Compression = 0 // BI_RGB
        };
        byte[] pixels = new byte[width * height * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    // ---------- Shell helpers ----------

    private static bool TryCreateItem(string parsingName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IShellItem? item)
    {
        Guid iid = typeof(IShellItem).GUID;
        int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object? result);
        item = hr == 0 ? result as IShellItem : null;
        return item is not null;
    }

    private static string? GetDisplayName(IShellItem item, SIGDN kind)
    {
        if (item.GetDisplayName(kind, out IntPtr buffer) != 0 || buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    private static string? GetString(IShellItem2? item, PropertyKey key)
    {
        if (item is null) return null;
        try
        {
            return item.GetString(ref key, out IntPtr buffer) == 0 && buffer != IntPtr.Zero ? TakeString(buffer) : null;
        }
        catch { return null; }
    }

    private static string? TakeString(IntPtr buffer)
    {
        try { return Marshal.PtrToStringUni(buffer); }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    /// <summary>Single background STA thread that runs queued Shell work in order.</summary>
    private sealed class StaWorker(string name)
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly object _startGate = new();
        private bool _started;

        public Task<T> Run<T>(Func<T> work)
        {
            EnsureStarted();
            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }

        private void EnsureStarted()
        {
            lock (_startGate)
            {
                if (_started) return;
                Thread thread = new(() =>
                {
                    foreach (Action action in _queue.GetConsumingEnumerable()) action();
                })
                { IsBackground = true, Name = name };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _started = true;
            }
        }
    }

    // ---------- Interop ----------

    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly PropertyKey PKEY_Link_TargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private static readonly PropertyKey PKEY_Link_Arguments = new(new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), 100);
    private static readonly PropertyKey PKEY_AppUserModel_PackageInstallPath = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 15);
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
        DESKTOPABSOLUTEPARSING = 0x80028000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        // BITMAPINFO's color table is unused for 32bpp BI_RGB; padding keeps GetDIBits safe if it writes one entry.
        public int ColorTablePadding;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? result);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(SIGDN kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem methods (COM interop ignores interface inheritance, so they are redeclared to line up the vtable)
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? result);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(SIGDN kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        // IShellItem2
        [PreserveSig] int GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr store);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid interfaceId, out IntPtr store);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid interfaceId, out IntPtr store);
        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey key, ref Guid interfaceId, out IntPtr list);
        [PreserveSig] int Update(IntPtr bindContext);
        [PreserveSig] int GetProperty(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetCLSID(ref PropertyKey key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PropertyKey key, out long fileTime);
        [PreserveSig] int GetInt32(ref PropertyKey key, out int value);
        [PreserveSig] int GetString(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int GetUInt32(ref PropertyKey key, out uint value);
        [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong value);
        [PreserveSig] int GetBool(ref PropertyKey key, out int value);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems? clone);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
