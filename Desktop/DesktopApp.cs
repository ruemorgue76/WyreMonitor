using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// The desktop app: the window and the icon next to the clock. It runs with the user's normal
/// rights. The sensors are read by the service; this part only shows the page the service serves.
/// </summary>
internal static class DesktopApp
{
    private const string InstanceName = $@"Local\{AppInfo.Name}.App";
    private const string ShowSignalName = $@"Local\{AppInfo.Name}.ShowWindow";

    public static int Run(string[] args)
    {
        var background = args.Contains(AppInfo.BackgroundFlag);
        var mini = args.Contains(AppInfo.MiniFlag);

        // One app per user session. A second launch (Start menu, desktop) asks the first one to
        // show its window through a named event, then exits.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        EventWaitHandle? showSignal = null;
        try
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Another program holds the name: the window can't be raised from a second launch.
        }

        if (!isFirst)
        {
            if (!background && !mini) showSignal?.Set();
            showSignal?.Dispose();
            return 0;
        }

        Toasts.Register();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // An error on the window's thread is logged instead of ending the app with a crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppInfo.LogError(e.Exception);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var tray = new TrayApp(showWindow: !background && !mini, showMini: mini);

        if (showSignal is not null)
        {
            new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    try { tray.ShowWindow(); }
                    catch (Exception) { return; }   // the app is closing
                }
            })
            { IsBackground = true, Name = "Show window signal" }.Start();
        }

        Application.Run(tray);
        return 0;
    }
}
