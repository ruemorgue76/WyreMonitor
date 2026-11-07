using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WyreMonitor.Desktop;

/// <summary>Finds a running instance and starts this app with admin rights.</summary>
internal static class Launcher
{
    // Only ever talks to 127.0.0.1: no proxy (looking one up can take seconds per request),
    // and a short connect timeout, since a free port refuses at once.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string MutexName => $@"Local\{AppInfo.Name}.Instance";
    private static string PortFile => Path.Combine(AppInfo.DataDir, "port");

    /// <summary>True when an instance holds the mutex. Instant, unlike probing ports.</summary>
    private static bool InstanceExists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // held by the elevated instance
        }
    }

    /// <summary>
    /// The port of a running WyreMonitor, or null. Checks the mutex first and then only the port
    /// the instance wrote down: on Windows a connection to a closed port takes about a second to
    /// fail, so probing the whole range made every launch slow.
    /// </summary>
    public static int? FindRunning()
    {
        if (!InstanceExists()) return null;
        try
        {
            if (!int.TryParse(File.ReadAllText(PortFile).Trim(), out var port)) return null;
            var json = Http.GetStringAsync($"http://127.0.0.1:{port}/api/health").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("app", out var app) && app.GetString() == AppInfo.Name ? port : null;
        }
        catch
        {
            return null;   // still starting: no port written yet, or the server isn't answering yet
        }
    }

    /// <summary>Written by the instance once its server listens; removed when it quits.</summary>
    public static void WritePort(int port)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(PortFile, port.ToString());
        }
        catch { /* a second launch then simply waits for the mutex holder */ }
    }

    public static void DeletePort()
    {
        try { File.Delete(PortFile); } catch { /* best effort */ }
    }

    public static int? WaitForRunning(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindRunning() is { } port) return port;
            Thread.Sleep(200);
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
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
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
