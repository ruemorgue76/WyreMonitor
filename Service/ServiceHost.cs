using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;
using WyreMonitor.Desktop;
using WyreMonitor.Hardware;

namespace WyreMonitor.Service;

/// <summary>
/// The privileged half of WyreMonitor: a Windows service running as LocalSystem.
/// It reads the sensors and serves the page and a read-only JSON API on 127.0.0.1.
/// It writes nothing where a normal user could interfere: its port goes to HKLM, which only
/// administrators can change, and its errors go to the Windows Event Log.
/// </summary>
internal static class ServiceHost
{
    // Strict by default: the page loads only its own files and talks only to this server.
    // Inline style attributes are allowed because the page sets a few layout variables.
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static int Run()
    {
        // A port of the usual range, or any free one if another program holds them all: the
        // port is published in HKLM anyway, so the app finds it either way.
        var port = FindFreePort() ?? 0;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // No command-line configuration. The host still reads DOTNET_ and ASPNETCORE_
            // variables, but a service only sees the machine environment, which only
            // administrators can change.
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Host.UseWindowsService(o => o.ServiceName = AppInfo.ServiceName);

        builder.Logging.ClearProviders();
        if (WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddEventLog(s => s.SourceName = AppInfo.ServiceName);
        else
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        // Requests whose Host is anything else are refused: this stops DNS rebinding, where a
        // web page points its own domain at 127.0.0.1 to read the API.
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

        // One instance serves both as the DI singleton and the background poller.
        builder.Services.AddSingleton<HardwareMonitorService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareMonitorService>());

        var app = builder.Build();
        // Backstop: a value that isn't a finite number is written as a string rather than making
        // the whole answer fail (sensor values are already filtered).
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        app.Use(async (context, next) =>
        {
            // Browsers say where a request comes from, and a page can't change it. Only the
            // service's own page ("same-origin") and an address typed or opened directly ("none")
            // get through; a web site can't even make the service read its sensors faster.
            // The app's own requests send no such header and pass too.
            var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Length > 0 && site is not ("same-origin" or "none"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            await next(context);
        });

        app.UseDefaultFiles();
        // no-cache: revalidate on every load, otherwise a new page can be paired with an
        // old app.js after an update.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        // The single data endpoint the page polls, once or twice per second. Requests also tell
        // the service a page is open, so it reads sensors at that pace instead of rarely.
        app.MapGet("/api/sensors", (HttpRequest request, HardwareMonitorService svc) =>
        {
            if (request.Query["background"] == "1")
            {
                // The icon next to the clock: it keeps the sensors read at the idle pace and
                // needs no history, which is most of the response.
                svc.NoteBackgroundActivity();
                var latest = svc.Latest;
                return Results.Json(latest with
                {
                    Hardware = latest.Hardware
                        .Select(g => g with { Sensors = g.Sensors.Select(s => s with { History = [] }).ToList() })
                        .ToList(),
                }, jsonOptions);
            }
            svc.NoteClientActivity(fast: request.Query["fast"] == "1");
            return Results.Json(svc.Latest, jsonOptions);
        });

        // The headline numbers only, for the small window (all parts) and the number on the tray
        // icon (?part=cpu|gpu|memory: only that part is read every second).
        app.MapGet("/api/summary", (HttpRequest request, HardwareMonitorService svc) =>
        {
            svc.NoteSummaryActivity(request.Query["part"].ToString() switch
            {
                "cpu" => HardwareMonitorService.SummaryPart.Cpu,
                "gpu" => HardwareMonitorService.SummaryPart.Gpu,
                "memory" => HardwareMonitorService.SummaryPart.Memory,
                _ => null,
            });
            var latest = svc.Latest;
            return Results.Json(new
            {
                latest.Timestamp,
                latest.Ready,
                latest.Elevated,
                Cpu = latest.Hardware.FirstOrDefault(h => h.Type == "Cpu")?.Name,
                latest.Summary,
            }, jsonOptions);
        });

        // Every sensor with its last 15 minutes, for the CSV export (asked for by the app).
        app.MapGet("/api/export", (HardwareMonitorService svc) =>
        {
            var latest = svc.Latest;
            return Results.Json(new
            {
                latest.Timestamp,
                Sensors = latest.Hardware.SelectMany(g => g.Sensors.Select(s => new
                {
                    s.Id, Group = g.Name, s.Name, s.Type, s.Unit, History = svc.History(s.Id) ?? [],
                })),
            }, jsonOptions);
        });

        // The last 15 minutes of one sensor, for the chart when it shows more than a minute.
        app.MapGet("/api/history", (HttpRequest request, HardwareMonitorService svc) =>
        {
            var id = request.Query["id"].ToString();
            var history = id.Length is > 0 and <= 256 ? svc.History(id) : null;
            return history is null ? Results.NotFound() : Results.Json(new { id, history }, jsonOptions);
        });

        // Liveness probe. "app" lets the desktop app tell this service from another program.
        app.MapGet("/api/health", (HardwareMonitorService svc) =>
            Results.Json(new { app = AppInfo.Name, version = AppInfo.Version, ok = true, elevated = svc.IsElevated }, jsonOptions));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            WritePort(address is not null ? new Uri(address).Port : port);
        });
        app.Lifetime.ApplicationStopping.Register(ClearPort);

        app.Run();
        return 0;
    }

    /// <summary>The first port of the range nobody else listens on.</summary>
    private static int? FindFreePort()
    {
        for (var port = AppInfo.FirstPort; port < AppInfo.FirstPort + AppInfo.PortCount; port++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // taken: try the next one
            }
        }
        return null;
    }

    private static void WritePort(int port)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AppInfo.RegistryKey);
            key.SetValue(AppInfo.PortValue, port, RegistryValueKind.DWord);
        }
        catch
        {
            // Not running as administrator (a development run): the desktop app then finds nothing.
        }
    }

    private static void ClearPort()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppInfo.RegistryKey, writable: true);
            key?.DeleteValue(AppInfo.PortValue, throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }
}
