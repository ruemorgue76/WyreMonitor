using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.Use(async (context, next) =>
        {
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

        // The single data endpoint the page polls once per second. Requests also tell the
        // service a page is open, so it reads sensors every second instead of rarely.
        app.MapGet("/api/sensors", (HardwareMonitorService svc) =>
        {
            svc.NoteClientActivity();
            return Results.Json(svc.Latest, jsonOptions);
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
