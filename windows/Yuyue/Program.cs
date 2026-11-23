using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Yuyue;

public static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            App.AppendCrashLog(ResolveCrashLogDir(args), "APPDOMAIN", "Unhandled domain exception", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            App.AppendCrashLog(ResolveCrashLogDir(args), "TASK", "Unobserved task exception", ev.Exception);
            ev.SetObserved();
        };

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            if (DecideRedirection(args))
                return;

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            string logDir = ResolveCrashLogDir(args);
            App.AppendCrashLog(logDir, "STARTUP_FATAL", "Fatal error during application startup", ex);
            try
            {
                MessageBoxW(
                    IntPtr.Zero,
                    $"语阅启动时遇到错误：{ex.Message}\n\n错误日志已保存到：\n{Path.Combine(logDir, "yuyue.log")}",
                    "语阅 Yuyue",
                    0x00000010); // MB_ICONERROR
            }
            catch { }
        }
    }

    private static string ResolveCrashLogDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--library" && !string.IsNullOrWhiteSpace(args[i + 1]))
                return Path.GetFullPath(args[i + 1]);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yuyue");
    }

    private static bool DecideRedirection(string[] args)
    {
        // When relaunching for language switch, wait briefly for the exiting process to release its instance key.
        bool isRelaunch = args.Any(a => a == "--relaunch");
        string key = BuildInstanceKey(args);

        for (int attempt = 0; attempt < (isRelaunch ? 8 : 1); attempt++)
        {
            try
            {
                var keyInstance = AppInstance.FindOrRegisterForKey(key);
                if (keyInstance.IsCurrent)
                    return false;

                if (isRelaunch && attempt < 7)
                {
                    Thread.Sleep(250);
                    continue;
                }

                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                Task.Run(() => keyInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait()).Wait(TimeSpan.FromSeconds(5));
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static string BuildInstanceKey(string[] args)
    {
        string? customLib = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--library")
            {
                customLib = Path.GetFullPath(args[i + 1]).ToLowerInvariant();
                break;
            }
        }
        if (string.IsNullOrEmpty(customLib))
            return "yuyue-desktop-main";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(customLib));
        return "yuyue-lib-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
