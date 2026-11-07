using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WyreMonitor.Desktop;

/// <summary>Finds a running instance and starts this app with admin rights.</summary>
internal static class Launcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The port of a running WyreMonitor, or null. Other programs on the ports are ignored.</summary>
    public static int? FindRunning()
    {
        for (var port = AppInfo.FirstPort; port < AppInfo.FirstPort + AppInfo.PortCount; port++)
        {
            try
            {
                var json = Http.GetStringAsync($"http://127.0.0.1:{port}/api/health").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("app", out var app) && app.GetString() == AppInfo.Name) return port;
            }
            catch { /* nothing there, or not us */ }
        }
        return null;
    }

    public static int? WaitForRunning(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindRunning() is { } port) return port;
            Thread.Sleep(300);
        }
        return null;
    }

    public static void RequestShow(int port)
    {
        // The server can answer a moment before the tray is ready, hence the retries.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/show");
                request.Headers.Add(AppInfo.ShowHeader, "1");
                using var response = Http.Send(request);
                if (response.IsSuccessStatusCode) return;
            }
            catch { /* retry below */ }
            Thread.Sleep(300);
        }
    }

    /// <summary>
    /// Held for the app's whole life by the instance that owns the server. Null when another
    /// instance already holds it (or holds it elevated, which denies access).
    /// </summary>
    public static Mutex? TryClaimInstance()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, $@"Local\{AppInfo.Name}.Instance", out var created);
            if (created) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool TryStartViaTask()
    {
        if (RunSchtasks($"/run /tn \"{AppInfo.LaunchTask}\"", out _) != 0) return false;

        // The task starts in the background: wait for it, then ask for the window.
        if (WaitForRunning(TimeSpan.FromSeconds(30)) is not { } port) return false;
        RequestShow(port);
        return true;
    }

    public static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = string.Join(' ', args),
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;   // UAC refused
        }
    }

    public static bool AutostartAvailable() =>
        RunSchtasks($"/query /tn \"{AppInfo.AutostartTask}\"", out _) == 0;

    public static bool AutostartEnabled()
    {
        if (RunSchtasks($"/query /tn \"{AppInfo.AutostartTask}\" /xml", out var xml) != 0) return false;
        // Settings/Enabled is the task-wide switch. When absent, the default is true.
        var match = Regex.Match(xml, @"<Settings>.*?<Enabled>(true|false)</Enabled>", RegexOptions.Singleline);
        return !match.Success || match.Groups[1].Value == "true";
    }

    public static bool SetAutostart(bool enabled) =>
        RunSchtasks($"/change /tn \"{AppInfo.AutostartTask}\" {(enabled ? "/enable" : "/disable")}", out _) == 0;

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
