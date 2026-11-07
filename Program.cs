using System.Windows.Forms;
using WyreMonitor.Desktop;
using WyreMonitor.Service;

namespace WyreMonitor;

/// <summary>
/// One executable, two roles, kept apart on purpose:
/// <list type="bullet">
/// <item><c>--service</c>: the Windows service. It reads the sensors as LocalSystem and serves
/// the page on 127.0.0.1. It takes nothing from any user's environment or profile.</item>
/// <item>no argument: the desktop app, always without administrator rights. It shows the page
/// in a window and keeps an icon next to the clock.</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Before any library loads a DLL by name: see NativeMethods.RestrictDllSearch.
        NativeMethods.RestrictDllSearch();

        if (args.Contains(AppInfo.ServiceFlag)) return ServiceHost.Run();

        try
        {
            return DesktopApp.Run(args);
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
            MessageBox.Show($"WyreMonitor could not start.\n\n{ex.Message}\n\nDetails in {AppInfo.LogPath}",
                "WyreMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
