using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Yuyue;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
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
