using System.Reflection;

namespace WyreMonitor.Desktop;

internal static class AppInfo
{
    public const string Name = "WyreMonitor";

    /// <summary>From &lt;Version&gt; in WyreMonitor.csproj, the only place the version is written.</summary>
    public static string Version { get; } = FormatVersion(Assembly.GetEntryAssembly()?.GetName().Version);

    private static string FormatVersion(Version? v) => v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";

    public const string ServiceFlag = "--service";
    public const string BackgroundFlag = "--background";
    /// <summary>Opens the small always-on-top window for this run only, without saving the choice.</summary>
    public const string MiniFlag = "--mini";
    public const string ServiceName = "WyreMonitor";

    // The service listens on the first free port from FirstPort on and writes it here.
    // HKLM: only administrators can change it, so the desktop app can trust it.
    public const int FirstPort = 5005;
    public const int PortCount = 10;
    public const string RegistryKey = @"SOFTWARE\WyreMonitor";
    public const string PortValue = "Port";

    /// <summary>The desktop app's own data: the window's browser profile and its error log.</summary>
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
