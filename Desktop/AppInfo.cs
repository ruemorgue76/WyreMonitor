using System.Reflection;

namespace WyreMonitor.Desktop;

internal static class AppInfo
{
    public const string Name = "WyreMonitor";

    /// <summary>From &lt;Version&gt; in WyreMonitor.csproj, the only place the version is written.</summary>
    public static string Version { get; } = FormatVersion(Assembly.GetEntryAssembly()?.GetName().Version);

    private static string FormatVersion(Version? v) => v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";

    // The app listens on the first free port from FirstPort on.
    public const int FirstPort = 5005;
    public const int PortCount = 10;
    public static int Port { get; set; } = FirstPort;
    public static string Url => $"http://127.0.0.1:{Port}";

    public const string BackgroundFlag = "--background";
    public const string NoElevateFlag = "--no-elevate";
    public const string ShowHeader = "X-WyreMonitor-Show";

    // Started on demand: gives a click on the shortcut admin rights with no UAC prompt.
    public const string LaunchTask = "WyreMonitor";
    // Same action, with a logon trigger. Enabled or disabled from the tray menu.
    public const string AutostartTask = "WyreMonitor Autostart";

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string LogPath { get; } = Path.Combine(DataDir, "errors.log");

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* the error is also shown on screen */ }
    }
}
