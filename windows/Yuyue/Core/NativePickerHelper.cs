using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Yuyue.Core;

/// <summary>
/// Crash-safe file and folder picker helper for unpackaged WinUI 3.
/// Tries WinRT <see cref="Windows.Storage.Pickers"/> when running non-elevated with a valid HWND,
/// and seamlessly falls back to native Win32 Explorer dialogs (<c>GetOpenFileNameW</c>,
/// <c>GetSaveFileNameW</c>, <c>IFileOpenDialog</c>) when running elevated (Administrator) or
/// when the out-of-proc WinRT picker broker throws <c>COMException (0x80070578)</c>.
/// </summary>
internal static class NativePickerHelper
{
    public static async Task<string?> PickSingleFileAsync(
        Window? owner,
        IEnumerable<string> extensions,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary,
        PickerViewMode viewMode = PickerViewMode.List)
    {
        if (Environment.GetEnvironmentVariable("YUYUE_PICKER_AUTO_CANCEL") == "1")
            return null;

        var extList = NormalizeExtensions(extensions);
        IntPtr hwnd = SafeGetHwnd(owner);
        return await PickSingleFileCoreAsync(hwnd, extList, startLocation, viewMode);
    }

    public static Task<string?> PickSingleFileAsync(
        IntPtr hwnd,
        IEnumerable<string> extensions,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary,
        PickerViewMode viewMode = PickerViewMode.List)
    {
        if (Environment.GetEnvironmentVariable("YUYUE_PICKER_AUTO_CANCEL") == "1")
            return Task.FromResult<string?>(null);

        IntPtr validHwnd = hwnd != IntPtr.Zero && IsWindow(hwnd) ? hwnd : IntPtr.Zero;
        return PickSingleFileCoreAsync(validHwnd, NormalizeExtensions(extensions), startLocation, viewMode);
    }

    private static async Task<string?> PickSingleFileCoreAsync(
        IntPtr hwnd,
        List<string> extList,
        PickerLocationId startLocation,
        PickerViewMode viewMode)
    {

        if (!Environment.IsPrivilegedProcess && hwnd != IntPtr.Zero)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = startLocation,
                    ViewMode = viewMode,
                };
                foreach (var ext in extList) picker.FileTypeFilter.Add(ext);
                InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (COMException)
            {
                // Fall through to native Win32 Explorer dialog
            }
            catch
            {
                return null;
            }
        }

        return ShowWin32OpenFiles(hwnd, extList, allowMultiSelect: false).FirstOrDefault();
    }

    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        Window? owner,
        IEnumerable<string> extensions,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary,
        PickerViewMode viewMode = PickerViewMode.List)
    {
        if (Environment.GetEnvironmentVariable("YUYUE_PICKER_AUTO_CANCEL") == "1")
            return [];

        var extList = NormalizeExtensions(extensions);
        IntPtr hwnd = SafeGetHwnd(owner);

        if (!Environment.IsPrivilegedProcess && hwnd != IntPtr.Zero)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = startLocation,
                    ViewMode = viewMode,
                };
                foreach (var ext in extList) picker.FileTypeFilter.Add(ext);
                InitializeWithWindow.Initialize(picker, hwnd);
                var files = await picker.PickMultipleFilesAsync();
                if (files is null || files.Count == 0) return [];
                return files.Where(f => !string.IsNullOrEmpty(f.Path)).Select(f => f.Path).ToArray();
            }
            catch (COMException)
            {
                // Fall through to native Win32 Explorer dialog
            }
            catch
            {
                return [];
            }
        }

        return ShowWin32OpenFiles(hwnd, extList, allowMultiSelect: true);
    }

    public static async Task<string?> PickSingleFolderAsync(
        Window? owner,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        if (Environment.GetEnvironmentVariable("YUYUE_PICKER_AUTO_CANCEL") == "1")
            return null;

        IntPtr hwnd = SafeGetHwnd(owner);

        if (!Environment.IsPrivilegedProcess && hwnd != IntPtr.Zero)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = startLocation };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, hwnd);
                var folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (COMException)
            {
                // Fall through to native Win32 IFileOpenDialog (FOS_PICKFOLDERS)
            }
            catch
            {
                return null;
            }
        }

        return ShowWin32PickFolder(hwnd);
    }

    public static async Task<string?> PickSaveFileAsync(
        Window? owner,
        string suggestedFileName,
        IEnumerable<(string Label, IReadOnlyList<string> Extensions)> fileTypeChoices,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        if (Environment.GetEnvironmentVariable("YUYUE_PICKER_AUTO_CANCEL") == "1")
            return null;

        var choices = fileTypeChoices.ToList();
        IntPtr hwnd = SafeGetHwnd(owner);

        if (!Environment.IsPrivilegedProcess && hwnd != IntPtr.Zero)
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = startLocation,
                    SuggestedFileName = suggestedFileName ?? "",
                };
                foreach (var (label, exts) in choices)
                    picker.FileTypeChoices.Add(label, exts.ToList());
                InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSaveFileAsync();
                return file?.Path;
            }
            catch (COMException)
            {
                // Fall through to native Win32 GetSaveFileNameW
            }
            catch
            {
                return null;
            }
        }

        return ShowWin32SaveFile(hwnd, suggestedFileName ?? "", choices);
    }

    private static IntPtr SafeGetHwnd(Window? owner)
    {
        if (owner is null) return IntPtr.Zero;
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(owner);
            return hwnd != IntPtr.Zero && IsWindow(hwnd) ? hwnd : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static List<string> NormalizeExtensions(IEnumerable<string> extensions)
    {
        var list = new List<string>();
        foreach (var raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string s = raw.Trim();
            if (s == "*") { list.Add("*"); continue; }
            if (!s.StartsWith('.')) s = "." + s;
            if (!list.Contains(s, StringComparer.OrdinalIgnoreCase))
                list.Add(s);
        }
        if (list.Count == 0) list.Add("*");
        return list;
    }

    private static IReadOnlyList<string> ShowWin32OpenFiles(IntPtr hwnd, List<string> extensions, bool allowMultiSelect)
    {
        const int BufferChars = 65536;
        IntPtr fileBuf = Marshal.AllocHGlobal(BufferChars * 2);
        IntPtr filterPtr = IntPtr.Zero;
        try
        {
            unsafe
            {
                new Span<byte>((void*)fileBuf, BufferChars * 2).Clear();
            }

            string filter;
            if (extensions.Count == 1 && extensions[0] == "*")
            {
                filter = $"{L10n.T("所有文件")}\0*.*\0\0";
            }
            else
            {
                string patterns = string.Join(";", extensions.Select(e => e == "*" ? "*.*" : $"*{e}"));
                filter = $"{L10n.T("支持的文件")}\0{patterns}\0{L10n.T("所有文件")}\0*.*\0\0";
            }
            filterPtr = Marshal.StringToHGlobalUni(filter);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = hwnd,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = fileBuf,
                nMaxFile = BufferChars,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                    | (allowMultiSelect ? OFN_ALLOWMULTISELECT : 0),
            };

            if (!GetOpenFileNameW(ref ofn))
                return [];

            return ParseOpenFileBuffer(fileBuf, BufferChars, allowMultiSelect);
        }
        catch
        {
            return [];
        }
        finally
        {
            if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(fileBuf);
        }
    }

    private static unsafe IReadOnlyList<string> ParseOpenFileBuffer(IntPtr buf, int maxChars, bool allowMultiSelect)
    {
        char* p = (char*)buf;
        var parts = new List<string>();
        int i = 0;
        while (i < maxChars && p[i] != '\0')
        {
            int start = i;
            while (i < maxChars && p[i] != '\0') i++;
            parts.Add(new string(p, start, i - start));
            i++; // skip single null
            if (!allowMultiSelect) break;
        }

        if (parts.Count == 0) return [];
        if (parts.Count == 1) return [parts[0]];

        string dir = parts[0];
        var files = new List<string>(parts.Count - 1);
        for (int k = 1; k < parts.Count; k++)
            files.Add(Path.Combine(dir, parts[k]));
        return files;
    }

    private static string? ShowWin32SaveFile(
        IntPtr hwnd,
        string suggestedFileName,
        List<(string Label, IReadOnlyList<string> Extensions)> choices)
    {
        const int BufferChars = 4096;
        IntPtr fileBuf = Marshal.AllocHGlobal(BufferChars * 2);
        IntPtr filterPtr = IntPtr.Zero;
        try
        {
            unsafe
            {
                new Span<byte>((void*)fileBuf, BufferChars * 2).Clear();
                string safeName = SanitizeSuggestedFileName(suggestedFileName);
                if (!string.IsNullOrEmpty(safeName))
                {
                    int copyLen = Math.Min(safeName.Length, BufferChars - 2);
                    safeName.AsSpan(0, copyLen).CopyTo(new Span<char>((void*)fileBuf, BufferChars));
                }
            }

            var filterSb = new System.Text.StringBuilder();
            string? firstExt = null;
            foreach (var (label, exts) in choices)
            {
                var norm = NormalizeExtensions(exts);
                firstExt ??= norm.FirstOrDefault()?.TrimStart('.');
                string pat = string.Join(";", norm.Select(e => e == "*" ? "*.*" : $"*{e}"));
                filterSb.Append(string.IsNullOrWhiteSpace(label) ? pat : label).Append('\0').Append(pat).Append('\0');
            }
            if (filterSb.Length == 0)
                filterSb.Append(L10n.T("所有文件")).Append("\0*.*\0");
            filterSb.Append('\0');
            filterPtr = Marshal.StringToHGlobalUni(filterSb.ToString());

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = hwnd,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = fileBuf,
                nMaxFile = BufferChars,
                lpstrDefExt = firstExt,
                Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (!GetSaveFileNameW(ref ofn))
                return null;

            return Marshal.PtrToStringUni(fileBuf);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(fileBuf);
        }
    }

    private static string SanitizeSuggestedFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string? ShowWin32PickFolder(IntPtr hwnd)
    {
        IFileOpenDialog? dlg = null;
        IShellItem? item = null;
        try
        {
            var clsid = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
            var iid = typeof(IFileOpenDialog).GUID;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out object raw);
            if (hr < 0 || raw is not IFileOpenDialog dialog)
                return null;

            dlg = dialog;
            dlg.GetOptions(out uint opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR | FOS_PATHMUSTEXIST);
            int showHr = dlg.Show(hwnd);
            if (showHr < 0) return null; // Cancelled (0x800704C7) or error

            dlg.GetResult(out item);
            if (item is null) return null;
            item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item is not null) try { Marshal.ReleaseComObject(item); } catch { }
            if (dlg is not null) try { Marshal.ReleaseComObject(dlg); } catch { }
        }
    }

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
