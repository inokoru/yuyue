using System.Runtime.InteropServices;
using Yuyue.Core;

namespace Yuyue.UI.Controls;

/// <summary>
/// Windows 11 Taskbar Thumbnail Toolbar (<c>ITaskbarList3</c>) integration:
/// adds Prev / Play-Pause / Next quick playback controls directly to the taskbar window preview thumbnail.
/// </summary>
public sealed class TaskbarThumbToolbar : IDisposable
{
    public const ushort IdPrev = 4001;
    public const ushort IdPlayPause = 4002;
    public const ushort IdNext = 4003;
    public const ushort IdMiniPlayer = 4004;

    private const uint THB_ICON = 0x00000002;
    private const uint THB_TOOLTIP = 0x00000004;
    private const uint THB_FLAGS = 0x00000008;
    private const uint THBF_ENABLED = 0x00000000;
    private const uint THBF_DISABLED = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndInsertBefore, uint dwReserved);
        [PreserveSig]
        int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButtons);
        [PreserveSig]
        int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButtons);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListCom { }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

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

    private readonly IntPtr _hwnd;
    private ITaskbarList3? _taskbar;
    private bool _added;
    private IntPtr _iconPrev;
    private IntPtr _iconPlay;
    private IntPtr _iconPause;
    private IntPtr _iconNext;

    public TaskbarThumbToolbar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarListCom();
            _taskbar.HrInit();
            _iconPrev = CreateGlyphIcon(GlyphKind.Prev);
            _iconPlay = CreateGlyphIcon(GlyphKind.Play);
            _iconPause = CreateGlyphIcon(GlyphKind.Pause);
            _iconNext = CreateGlyphIcon(GlyphKind.Next);
        }
        catch
        {
            _taskbar = null;
        }
    }

    public void Reset() => _added = false;

    public void EnsureAdded(bool hasAudio, bool isPlaying)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        try
        {
            uint flags = hasAudio ? THBF_ENABLED : THBF_DISABLED;
            var buttons = new[]
            {
                new THUMBBUTTON
                {
                    dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                    iId = IdPrev,
                    hIcon = _iconPrev,
                    szTip = L10n.T("上一句 / 后退"),
                    dwFlags = flags,
                },
                new THUMBBUTTON
                {
                    dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                    iId = IdPlayPause,
                    hIcon = isPlaying ? _iconPause : _iconPlay,
                    szTip = L10n.T(isPlaying ? "暂停朗读" : "开始/继续朗读"),
                    dwFlags = flags,
                },
                new THUMBBUTTON
                {
                    dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
                    iId = IdNext,
                    hIcon = _iconNext,
                    szTip = L10n.T("下一句 / 前进"),
                    dwFlags = flags,
                },
            };

            if (!_added)
            {
                int hr = _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
                if (hr >= 0) _added = true;
            }
            else
            {
                _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            }
        }
        catch { }
    }

    private enum GlyphKind { Prev, Play, Pause, Next }

    private static IntPtr CreateGlyphIcon(GlyphKind kind)
    {
        const int sz = 16;
        uint[] pixels = new uint[sz * sz];
        const uint white = 0xFFF2EFEA;

        void SetPx(int x, int y)
        {
            if (x >= 0 && x < sz && y >= 0 && y < sz)
                pixels[y * sz + x] = white;
        }

        switch (kind)
        {
            case GlyphKind.Play:
                for (int y = 3; y <= 12; y++)
                {
                    int half = Math.Abs(y - 7);
                    int maxX = 12 - (int)(half * 1.3);
                    for (int x = 4; x <= maxX; x++) SetPx(x, y);
                }
                break;
            case GlyphKind.Pause:
                for (int y = 3; y <= 12; y++)
                {
                    for (int x = 4; x <= 6; x++) SetPx(x, y);
                    for (int x = 9; x <= 11; x++) SetPx(x, y);
                }
                break;
            case GlyphKind.Prev:
                for (int y = 3; y <= 12; y++)
                {
                    SetPx(3, y); SetPx(4, y);
                    int half = Math.Abs(y - 7);
                    int minX = 6 + (int)(half * 1.2);
                    for (int x = minX; x <= 12; x++) SetPx(x, y);
                }
                break;
            case GlyphKind.Next:
                for (int y = 3; y <= 12; y++)
                {
                    SetPx(11, y); SetPx(12, y);
                    int half = Math.Abs(y - 7);
                    int maxX = 9 - (int)(half * 1.2);
                    for (int x = 3; x <= maxX; x++) SetPx(x, y);
                }
                break;
        }

        IntPtr hColor = IntPtr.Zero;
        IntPtr hMask = IntPtr.Zero;
        byte[] maskBits = new byte[sz * 4];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var maskPin = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
        try
        {
            hColor = CreateBitmap(sz, sz, 1, 32, pin.AddrOfPinnedObject());
            hMask = CreateBitmap(sz, sz, 1, 1, maskPin.AddrOfPinnedObject());
            var info = new ICONINFO { fIcon = true, hbmColor = hColor, hbmMask = hMask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            pin.Free();
            maskPin.Free();
            if (hColor != IntPtr.Zero) DeleteObject(hColor);
            if (hMask != IntPtr.Zero) DeleteObject(hMask);
        }
    }

    public void Dispose()
    {
        if (_iconPrev != IntPtr.Zero) { DestroyIcon(_iconPrev); _iconPrev = IntPtr.Zero; }
        if (_iconPlay != IntPtr.Zero) { DestroyIcon(_iconPlay); _iconPlay = IntPtr.Zero; }
        if (_iconPause != IntPtr.Zero) { DestroyIcon(_iconPause); _iconPause = IntPtr.Zero; }
        if (_iconNext != IntPtr.Zero) { DestroyIcon(_iconNext); _iconNext = IntPtr.Zero; }
        _taskbar = null;
    }
}
