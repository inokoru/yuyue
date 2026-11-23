using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Yuyue.Core;

/// <summary>
/// Stores API keys and WebDAV passwords encrypted with Windows DPAPI (CurrentUser scope).
/// Never leaves this machine and never enters the sync operation log.
/// </summary>
public sealed class SecretStore
{
    private readonly string _path;
    private Dictionary<string, string> _cache = new();

    public SecretStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "secrets.dpapi");
        Load();
    }

    public string Get(string key) => _cache.TryGetValue(key, out var val) ? val : "";

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) _cache.Remove(key);
        else _cache[key] = value;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var plain = Unprotect(encrypted);
            if (plain is not null)
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? new();
            }
        }
        catch
        {
            _cache = new();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache);
            var plain = Encoding.UTF8.GetBytes(json);
            var encrypted = Protect(plain);
            if (encrypted is not null)
            {
                File.WriteAllBytes(_path, encrypted);
            }
        }
        catch { /* best effort */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[]? Protect(byte[] plain)
    {
        var inBlob = new DATA_BLOB { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, inBlob.pbData, plain.Length);
            if (!CryptProtectData(ref inBlob, "Yuyue", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); }
    }

    private static byte[]? Unprotect(byte[] cipher)
    {
        var inBlob = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, inBlob.pbData, cipher.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); }
    }
}
