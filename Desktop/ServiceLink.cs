using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace WyreMonitor.Desktop;

/// <summary>Finds the WyreMonitor service and manages the start with Windows setting.</summary>
internal static class ServiceLink
{
    // Only ever talks to 127.0.0.1: no proxy, and a short connect timeout.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// The page's address, or null while the service is not running. The port comes from HKLM,
    /// which only administrators can write. The program listening on it must be the WyreMonitor
    /// service itself: after a crash, another program could grab the port before the service
    /// restarts, and its page would otherwise appear inside this window.
    /// </summary>
    public static async Task<string?> FindUrlAsync()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppInfo.RegistryKey);
            if (key?.GetValue(AppInfo.PortValue) is not int port || port < 1 || port > 65535) return null;

            var servicePid = ServiceProcessId();
            if (servicePid is null || NativeMethods.ListeningProcess(port) != servicePid) return null;

            var url = $"http://127.0.0.1:{port}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{url}/api/health"));
            return doc.RootElement.TryGetProperty("app", out var app) && app.GetString() == AppInfo.Name ? url : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A request to the service, at an address returned by <see cref="FindUrlAsync"/>.</summary>
    public static Task<string> GetStringAsync(string url) => Http.GetStringAsync(url);

    /// <summary>The process id of the running WyreMonitor service, from the service manager.</summary>
    private static int? ServiceProcessId()
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT ProcessId, State FROM Win32_Service WHERE Name = '{AppInfo.ServiceName}'");
        foreach (var service in searcher.Get().Cast<ManagementObject>())
        {
            using (service)
            {
                if ((string)service["State"] == "Running" && service["ProcessId"] is uint pid && pid != 0) return (int)pid;
            }
        }
        return null;
    }

    // Start with Windows: a per-user Run entry that opens only the icon next to the clock.
    // It needs no administrator rights, and the user can also turn it off in Task Manager.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInfo.Name) is string;
        }
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppInfo.Name, $"\"{Environment.ProcessPath}\" {AppInfo.BackgroundFlag}");
        else key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
    }
}
