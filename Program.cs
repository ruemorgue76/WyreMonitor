using System.Text.Json;
using System.Windows.Forms;
using WyreMonitor.Desktop;
using WyreMonitor.Hardware;

namespace WyreMonitor;

internal static class Program
{
    // Set once the tray exists; called from Kestrel threads, TrayApp marshals to the UI thread.
    private static Action? _showWindow;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
            MessageBox.Show($"WyreMonitor could not start.\n\n{ex.Message}\n\nDetails in {AppInfo.LogPath}",
                "WyreMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var background = args.Contains(AppInfo.BackgroundFlag);

        // Second launch (Start menu, desktop): bring the running instance to the front.
        if (Launcher.FindRunning() is { } running)
        {
            if (!background) Launcher.RequestShow(running);
            return 0;
        }

        // The exe runs unelevated when clicked. The scheduled task starts it with admin
        // rights and no UAC prompt; if the task is missing, fall back to the UAC prompt.
        if (!Launcher.IsElevated() && !args.Contains(AppInfo.NoElevateFlag))
        {
            if (Launcher.TryStartViaTask()) return 0;
            if (Launcher.TryRelaunchElevated(args)) return 0;
            // UAC refused: keep going, the page explains what is missing.
        }

        // Two launches at the same moment: only one becomes the app, the other shows it.
        using var instance = Launcher.TryClaimInstance();
        if (instance is null)
        {
            if (!background && Launcher.WaitForRunning(TimeSpan.FromSeconds(30)) is { } port) Launcher.RequestShow(port);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var server = StartServer();

        using (var tray = new TrayApp(showWindow: !background))
        {
            _showWindow = tray.ShowWindow;
            Application.Run(tray);
            _showWindow = null;
        }

        // Closes the hardware session and unloads the driver cleanly.
        server.StopAsync().Wait(TimeSpan.FromSeconds(3));
        server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        return 0;
    }

    /// <summary>Listens on the first free port of the range: another program may own the default one.</summary>
    private static WebApplication StartServer()
    {
        for (var port = AppInfo.FirstPort; port < AppInfo.FirstPort + AppInfo.PortCount; port++)
        {
            var server = BuildServer(port);
            try
            {
                server.StartAsync().GetAwaiter().GetResult();
                AppInfo.Port = port;
                return server;
            }
            catch (IOException)
            {
                server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
        }
        throw new InvalidOperationException(
            $"Ports {AppInfo.FirstPort}-{AppInfo.FirstPort + AppInfo.PortCount - 1} are all in use by other programs.");
    }

    private static WebApplication BuildServer(int port)
    {
        // ContentRoot = next to the exe, so the page is found wherever the app is started from.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();

        // One instance serves both as the DI singleton and the background poller.
        builder.Services.AddSingleton<HardwareMonitorService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareMonitorService>());

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.UseDefaultFiles();
        // no-cache: revalidate on every load, otherwise a new page can be paired with an
        // old app.js after an update.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        // The single data endpoint the dashboard polls once per second. Requests also tell the
        // service a page is open, so it reads sensors every second instead of every few.
        app.MapGet("/api/sensors", (HardwareMonitorService svc) =>
        {
            svc.NoteClientActivity();
            return Results.Json(svc.Latest, jsonOptions);
        });

        // Liveness probe. "app" lets a second launch tell this app from another program on the port.
        app.MapGet("/api/health", (HardwareMonitorService svc) =>
            Results.Json(new { app = AppInfo.Name, version = AppInfo.Version, ok = true, elevated = svc.IsElevated }, jsonOptions));

        // A second launch asks the running instance to show its window. The custom header
        // stops web pages from triggering it: browsers cannot send it across origins.
        app.MapPost("/api/show", (HttpRequest request) =>
        {
            if (!request.Headers.ContainsKey(AppInfo.ShowHeader)) return Results.StatusCode(403);
            _showWindow?.Invoke();
            return Results.NoContent();
        });

        return app;
    }
}
