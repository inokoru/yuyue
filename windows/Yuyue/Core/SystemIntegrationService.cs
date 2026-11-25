using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Yuyue.Core;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/MacMenuBar.swift</c>, <c>AppLanguage.swift</c>, and Windows system integration:
/// 1. <c>SetThreadExecutionState</c> preventing system sleep while audio is playing (<c>KeepAwakeWhileListening</c>).
/// 2. <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> login background startup (<c>--background</c>).
/// 3. Dynamic Win32 <c>Shell_NotifyIconW</c> System Tray icon with 12 fps animated 3-bar equalizer &amp; context menu.
/// 4. Bilingual UI localization (<c>L10n</c>) &amp; clean process relaunch on language switch.
/// </summary>
public sealed class SystemIntegrationService : IDisposable
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Yuyue";

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_APP_TRAY = 0x8000 + 42;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private readonly LibraryService _library;
    private readonly HashSet<object> _activeAudioSources = new();
    private readonly Dictionary<object, (string? Title, Action? Toggle)> _sourceInfo = new();
    private object? _currentPlaybackSource;
    private readonly object _trayLock = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private WndProcDelegate? _wndProc;
    private IntPtr _msgHwnd;
    private IntPtr _currentIcon;
    private bool _trayAdded;
    private bool _disposed;
    private System.Threading.Timer? _animTimer;
    private readonly Stopwatch _animClock = Stopwatch.StartNew();

    public string? ActivePlaybackTitle { get; private set; }
    public bool IsAudioPlaying { get; private set; }
    public event Action? PlaybackStateChanged;
    public bool IsPlayingTitle(string? title) =>
        IsAudioPlaying && !string.IsNullOrWhiteSpace(title)
        && string.Equals(ActivePlaybackTitle, title, StringComparison.OrdinalIgnoreCase);
    public Action? TogglePlaybackCallback { get; set; }
    public Action? ShowShelfCallback { get; set; }
    public Action? OpenSettingsCallback { get; set; }
    public Action? ExitAppCallback { get; set; }
    public Action<string>? PlayPodcastIssueCallback { get; set; }

    public SystemIntegrationService(LibraryService library)
    {
        _library = library;
        try { _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); } catch { }
    }

    public void AttachUiQueue(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        _uiQueue = queue;
        SyncLoginStartup(_library.Prefs.AutoLaunchOnLogin);
    }

    public void SetAudioPlaying(object source, bool playing, string? title = null, Action? toggleCallback = null)
    {
        lock (_activeAudioSources)
        {
            _sourceInfo.TryGetValue(source, out var prev);
            string? resolvedTitle = !string.IsNullOrWhiteSpace(title) ? title : prev.Title;
            Action? resolvedToggle = toggleCallback ?? prev.Toggle;
            if (resolvedTitle is not null || resolvedToggle is not null)
                _sourceInfo[source] = (resolvedTitle, resolvedToggle);

            if (playing)
            {
                _activeAudioSources.Add(source);
                _currentPlaybackSource = source;
                if (!string.IsNullOrWhiteSpace(resolvedTitle)) ActivePlaybackTitle = resolvedTitle;
                if (resolvedToggle is not null) TogglePlaybackCallback = resolvedToggle;
            }
            else
            {
                _activeAudioSources.Remove(source);
                if (_activeAudioSources.Count > 0 && Equals(_currentPlaybackSource, source))
                {
                    var next = _activeAudioSources.First();
                    _currentPlaybackSource = next;
                    if (_sourceInfo.TryGetValue(next, out var nextInfo))
                    {
                        ActivePlaybackTitle = nextInfo.Title;
                        TogglePlaybackCallback = nextInfo.Toggle;
                    }
                }
            }
            IsAudioPlaying = _activeAudioSources.Count > 0;
        }

        ApplyPowerStayAwake();
        UpdateTrayState();
        NotifyPlaybackStateChanged();
    }

    public void ClearAudioSource(string source) => ClearAudioSource((object?)source);

    public void ClearAudioSource(object? source)
    {
        lock (_activeAudioSources)
        {
            if (source is null)
            {
                _activeAudioSources.Clear();
                _sourceInfo.Clear();
                _currentPlaybackSource = null;
                ActivePlaybackTitle = null;
                TogglePlaybackCallback = null;
                IsAudioPlaying = false;
            }
            else
            {
                var toRemove = new List<object> { source };
                string? strKey = source as string;
                foreach (var kv in _sourceInfo)
                {
                    if (Equals(kv.Key, source))
                    {
                        toRemove.Add(kv.Key);
                    }
                    else if (!string.IsNullOrWhiteSpace(strKey))
                    {
                        if ((kv.Key is string ks && string.Equals(ks, strKey, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrEmpty(kv.Value.Title) && string.Equals(kv.Value.Title, strKey, StringComparison.OrdinalIgnoreCase))
                            || kv.Key.GetType().Name.Contains(strKey, StringComparison.OrdinalIgnoreCase))
                        {
                            toRemove.Add(kv.Key);
                        }
                    }
                }

                bool clearedCurrent = false;
                foreach (var k in toRemove)
                {
                    if (Equals(_currentPlaybackSource, k)) clearedCurrent = true;
                    _activeAudioSources.Remove(k);
                    _sourceInfo.Remove(k);
                }

                if (!string.IsNullOrWhiteSpace(strKey)
                    && !string.IsNullOrEmpty(ActivePlaybackTitle)
                    && string.Equals(ActivePlaybackTitle, strKey, StringComparison.OrdinalIgnoreCase))
                {
                    clearedCurrent = true;
                }

                if (clearedCurrent || _sourceInfo.Count == 0)
                {
                    if (_activeAudioSources.Count > 0)
                    {
                        var next = _activeAudioSources.First();
                        _currentPlaybackSource = next;
                        if (_sourceInfo.TryGetValue(next, out var nextInfo))
                        {
                            ActivePlaybackTitle = nextInfo.Title;
                            TogglePlaybackCallback = nextInfo.Toggle;
                        }
                        else
                        {
                            ActivePlaybackTitle = null;
                            TogglePlaybackCallback = null;
                        }
                    }
                    else
                    {
                        _currentPlaybackSource = null;
                        ActivePlaybackTitle = null;
                        TogglePlaybackCallback = null;
                    }
                }

                IsAudioPlaying = _activeAudioSources.Count > 0;
            }
        }

        ApplyPowerStayAwake();
        UpdateTrayState();
        NotifyPlaybackStateChanged();
    }

    private void NotifyPlaybackStateChanged()
    {
        var handler = PlaybackStateChanged;
        if (handler is null) return;
        if (_uiQueue is not null)
            _uiQueue.TryEnqueue(() => { try { handler(); } catch { } });
        else
            try { handler(); } catch { }
    }

    public void ApplyPowerStayAwake()
    {
        try
        {
            if (IsAudioPlaying && _library.Prefs.KeepAwakeWhileListening)
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            else
                SetThreadExecutionState(ES_CONTINUOUS);
        }
        catch { }
    }

    public static void SyncLoginStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    key.SetValue(RunValueName, $"\"{exe}\" --background");
            }
            else
            {
                if (key.GetValue(RunValueName) is not null)
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public Func<bool>? IsMainWindowHiddenInTray { get; set; }

    public void EnsureTrayVisible(bool forceVisible = false)
    {
        bool shouldShow = forceVisible
            || (IsMainWindowHiddenInTray?.Invoke() == true)
            || IsAudioPlaying
            || !string.IsNullOrEmpty(ActivePlaybackTitle)
            || _library.Prefs.AutoLaunchOnLogin;
        RunOnTrayThread(() =>
        {
            if (shouldShow) ShowOrUpdateTrayLocked();
            else RemoveTrayLocked();
        });
    }

    private void RunOnTrayThread(Action action)
    {
        if (_disposed) return;
        var q = _uiQueue;
        if (q is not null && !q.HasThreadAccess)
        {
            q.TryEnqueue(() =>
            {
                lock (_trayLock)
                {
                    if (!_disposed) action();
                }
            });
            return;
        }
        lock (_trayLock)
        {
            if (!_disposed) action();
        }
    }

    private void UpdateTrayState()
    {
        EnsureTrayVisible();
        lock (_trayLock)
        {
            if (!_disposed && IsAudioPlaying)
            {
                _animTimer ??= new System.Threading.Timer(_ => TickTrayAnimation(), null, 80, 85); // ~12 fps per MacMenuBar.swift
            }
            else
            {
                _animTimer?.Dispose();
                _animTimer = null;
                if (_trayAdded) RunOnTrayThread(() => ShowOrUpdateTrayLocked(frameTime: null));
            }
        }
    }

    private void TickTrayAnimation()
    {
        if (_disposed || !IsAudioPlaying) return;
        double t = _animClock.Elapsed.TotalSeconds;
        RunOnTrayThread(() =>
        {
            if (_trayAdded && IsAudioPlaying) ShowOrUpdateTrayLocked(t);
        });
    }

    private void ShowOrUpdateTrayLocked(double? frameTime = null)
    {
        try
        {
            if (_msgHwnd == IntPtr.Zero)
            {
                _wndProc = OnTrayWndProc;
                string cls = "YuyueTrayMsgWnd_" + Environment.ProcessId;
                var wc = new WNDCLASS { lpfnWndProc = _wndProc, lpszClassName = cls };
                RegisterClassW(ref wc);
                _msgHwnd = CreateWindowExW(0, cls, "YuyueTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }

            IntPtr newIcon = RenderTrayHIcon(frameTime);
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _msgHwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_APP_TRAY,
                hIcon = newIcon,
                szTip = string.IsNullOrEmpty(ActivePlaybackTitle)
                    ? "语阅"
                    : $"语阅 · {(IsAudioPlaying ? "正在播放" : "已暂停")}：{ActivePlaybackTitle}",
                szInfo = "",
                szInfoTitle = "",
            };

            Shell_NotifyIconW(_trayAdded ? NIM_MODIFY : NIM_ADD, ref nid);
            _trayAdded = true;

            if (_currentIcon != IntPtr.Zero) DestroyIcon(_currentIcon);
            _currentIcon = newIcon;
        }
        catch { }
    }

    private void RemoveTrayLocked()
    {
        _animTimer?.Dispose();
        _animTimer = null;
        if (!_trayAdded || _msgHwnd == IntPtr.Zero) return;
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _msgHwnd,
                uID = 1,
                szTip = "",
                szInfo = "",
                szInfoTitle = "",
            };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
        }
        catch { }
        _trayAdded = false;
    }

    // 1:1 MacMenuBar.swift: 32x32 ARGB tray icon with 门 frame + 3 oscillating equalizer bars at periods [0.62, 0.9, 0.74]
    private static unsafe IntPtr RenderTrayHIcon(double? t)
    {
        const int S = 32;
        uint[] px = new uint[S * S];

        static uint Premul(byte a, byte r, byte g, byte b) =>
            ((uint)a << 24) | ((uint)(r * a / 255) << 16) | ((uint)(g * a / 255) << 8) | (uint)(b * a / 255);

        static void Blend(uint[] buf, int x, int y, byte a, byte r, byte g, byte b)
        {
            if ((uint)x >= S || (uint)y >= S || a == 0) return;
            int idx = y * S + x;
            uint dst = buf[idx];
            if (dst == 0 || a == 255) { buf[idx] = Premul(a, r, g, b); return; }
            int inv = 255 - a;
            uint da = (uint)Math.Min(255, a + (((dst >> 24) & 0xFF) * inv / 255));
            uint dr = (uint)Math.Min(255, (r * a / 255) + (((dst >> 16) & 0xFF) * inv / 255));
            uint dg = (uint)Math.Min(255, (g * a / 255) + (((dst >> 8) & 0xFF) * inv / 255));
            uint db = (uint)Math.Min(255, (b * a / 255) + ((dst & 0xFF) * inv / 255));
            buf[idx] = (da << 24) | (dr << 16) | (dg << 8) | db;
        }

        // Anti-aliased warm-paper disc
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                double dist = Math.Sqrt((x - 15.5) * (x - 15.5) + (y - 15.5) * (y - 15.5));
                if (dist <= 15.0)
                {
                    double cov = Math.Clamp(15.0 - dist, 0.0, 1.0);
                    Blend(px, x, y, (byte)(248 * cov), 250, 248, 244);
                }
            }
        }

        static void DrawThickSeg(uint[] buf, double x0, double y0, double x1, double y1, double radius, byte r, byte g, byte b)
        {
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - radius - 1));
            int maxX = Math.Min(S - 1, (int)Math.Ceiling(Math.Max(x0, x1) + radius + 1));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - radius - 1));
            int maxY = Math.Min(S - 1, (int)Math.Ceiling(Math.Max(y0, y1) + radius + 1));
            double dx = x1 - x0, dy = y1 - y0;
            double len2 = dx * dx + dy * dy;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    double u = len2 > 1e-6 ? Math.Clamp(((x - x0) * dx + (y - y0) * dy) / len2, 0.0, 1.0) : 0.0;
                    double cx = x0 + u * dx, cy = y0 + u * dy;
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= radius + 0.8)
                    {
                        double cov = Math.Clamp(radius + 0.6 - d, 0.0, 1.0);
                        Blend(buf, x, y, (byte)(255 * cov), r, g, b);
                    }
                }
            }
        }

        // Outer 门 arch
        DrawThickSeg(px, 7.5, 24.5, 7.5, 8.5, 1.25, 38, 38, 42);
        DrawThickSeg(px, 7.5, 8.5, 24.5, 8.5, 1.25, 38, 38, 42);
        DrawThickSeg(px, 24.5, 8.5, 24.5, 24.5, 1.25, 38, 38, 42);

        double[] xs = [11.6, 16.0, 20.4];
        double[] halves = [3.2, 6.0, 4.2];
        double[] periods = [0.62, 0.90, 0.74];
        double[] phases = [0.0, 1.9, 3.7];
        byte br = t is not null ? (byte)138 : (byte)38;
        byte bg = t is not null ? (byte)101 : (byte)38;
        byte bb = t is not null ? (byte)58 : (byte)42;

        for (int i = 0; i < 3; i++)
        {
            double scale = t is double sec
                ? 0.8 + 0.32 * Math.Sin(2.0 * Math.PI * sec / periods[i] + phases[i])
                : 0.72;
            double half = halves[i] * scale;
            DrawThickSeg(px, xs[i], 18.0 - half, xs[i], 18.0 + half, 1.25, br, bg, bb);
        }

        byte[] mask = new byte[S * 4]; // 32x32 1bpp mask (all 0 = use alpha channel)
        fixed (uint* pColor = px)
        fixed (byte* pMask = mask)
        {
            IntPtr hColor = CreateBitmap(S, S, 1, 32, (IntPtr)pColor);
            IntPtr hMask = CreateBitmap(S, S, 1, 1, (IntPtr)pMask);
            try
            {
                var ii = new ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = hColor };
                return CreateIconIndirect(ref ii);
            }
            finally
            {
                if (hColor != IntPtr.Zero) DeleteObject(hColor);
                if (hMask != IntPtr.Zero) DeleteObject(hMask);
            }
        }
    }

    private IntPtr OnTrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP)
            {
                ShowShelfCallback?.Invoke();
                return IntPtr.Zero;
            }
            if (ev == WM_RBUTTONUP)
            {
                ShowTrayPopupMenu();
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowTrayPopupMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var deskIssues = _library.Podcasts.GetIssues(PodcastPlacement.Desk)
            .Where(i => string.IsNullOrWhiteSpace(ActivePlaybackTitle)
                || !string.Equals(i.Title, ActivePlaybackTitle, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToArray();
        try
        {
            if (!string.IsNullOrEmpty(ActivePlaybackTitle))
            {
                string statePrefix = IsAudioPlaying ? L10n.Tr("正在放：", "Playing: ") : L10n.Tr("暂停在：", "Paused: ");
                AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, statePrefix + ActivePlaybackTitle);
                AppendMenuW(menu, MF_STRING, 1, IsAudioPlaying ? L10n.Tr("暂停", "Pause") : L10n.Tr("接着放", "Resume"));
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
            }

            if (deskIssues.Length > 0)
            {
                AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L10n.Tr("书桌待听", "On Your Desk"));
                for (int i = 0; i < deskIssues.Length; i++)
                {
                    string t = deskIssues[i].Title;
                    if (t.Length > 26) t = t[..26] + "…";
                    AppendMenuW(menu, MF_STRING, (nuint)(100 + i), "  " + t);
                }
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
            }

            AppendMenuW(menu, MF_STRING, 2, L10n.Tr("打开书架", "Open Shelf"));
            AppendMenuW(menu, MF_STRING, 3, L10n.Tr("设置…", "Settings…"));
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, 4, L10n.Tr("退出语阅", "Quit Yuyue"));

            GetCursorPos(out var pt);
            SetForegroundWindow(_msgHwnd);
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _msgHwnd, IntPtr.Zero);
            if (cmd == 1) TogglePlaybackCallback?.Invoke();
            else if (cmd == 2) ShowShelfCallback?.Invoke();
            else if (cmd == 3) OpenSettingsCallback?.Invoke();
            else if (cmd == 4) ExitAppCallback?.Invoke();
            else if (cmd >= 100 && cmd < 100 + deskIssues.Length)
                PlayPodcastIssueCallback?.Invoke(deskIssues[cmd - 100].Id);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public static void RelaunchApplication(string? extraArgs = null)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        try
        {
            string args = string.IsNullOrWhiteSpace(extraArgs) ? "--relaunch" : $"--relaunch {extraArgs}";
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = true,
            };
            Process.Start(psi);
            Environment.Exit(0);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_trayLock)
        {
            _disposed = true;
            try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
            RemoveTrayLocked();
            if (_currentIcon != IntPtr.Zero) { DestroyIcon(_currentIcon); _currentIcon = IntPtr.Zero; }
            if (_msgHwnd != IntPtr.Zero) { DestroyWindow(_msgHwnd); _msgHwnd = IntPtr.Zero; }
        }
    }
}

