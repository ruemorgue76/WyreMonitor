using System.Text.Json;

namespace WyreMonitor.Desktop;

/// <summary>
/// The desktop app's settings, per user, in %LOCALAPPDATA%\WyreMonitor\settings.json: what the
/// icon next to the clock does, and a copy of the page settings it needs (sent by the page,
/// see <see cref="PageSettings.FromMessage"/>).
/// </summary>
internal sealed class AppSettings
{
    /// <summary>The reading drawn on the tray icon (a <see cref="TrayValues"/> key), or null for none.</summary>
    public string? TrayValue { get; set; }
    /// <summary>Before 1.4.2: true meant the CPU temperature on the icon. Read once, then dropped.</summary>
    public bool? TrayTemperature { get; set; }
    public bool Notifications { get; set; }
    public bool MiniWindow { get; set; }
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }
    public bool AutoUpdateCheck { get; set; }
    public DateTime LastUpdateCheck { get; set; }
    public string? AnnouncedVersion { get; set; }   // a new version is announced once
    public PageSettings Page { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
                settings.Page = PageSettings.Sanitize(settings.Page);
                if (settings.TrayTemperature == true && settings.TrayValue is null) settings.TrayValue = "cpuTemp";
                settings.TrayTemperature = null;
                if (TrayValues.Find(settings.TrayValue) is null) settings.TrayValue = null;
                return settings;
            }
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);   // a damaged file: back to the defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
        }
    }
}

/// <summary>The readings the tray icon can show: the small window's numbers.</summary>
internal static class TrayValues
{
    public enum Kind { Temperature, Percent, Watts }

    /// <param name="Part">What the service must read for it: cpu, gpu or memory.</param>
    /// <param name="Field">Its name in the summary from /api/summary.</param>
    public sealed record Value(string Key, string Label, string Part, string Field, Kind Kind);

    public static readonly Value[] All =
    [
        new("cpuTemp", "CPU temperature", "cpu", "cpuTemp", Kind.Temperature),
        new("cpuLoad", "CPU load", "cpu", "cpuLoad", Kind.Percent),
        new("cpuPower", "CPU power", "cpu", "cpuPower", Kind.Watts),
        new("gpuTemp", "GPU temperature", "gpu", "gpuTemp", Kind.Temperature),
        new("gpuLoad", "GPU load", "gpu", "gpuLoad", Kind.Percent),
        new("gpuPower", "GPU power", "gpu", "gpuPower", Kind.Watts),
        new("ramLoad", "Memory use", "memory", "ramLoad", Kind.Percent),
    ];

    public static Value? Find(string? key) => All.FirstOrDefault(v => v.Key == key);
}

/// <summary>The page settings the icon next to the clock uses for its temperature and alerts.</summary>
internal sealed class PageSettings
{
    public const int MaxMessage = 64 * 1024;
    private const int MaxEntries = 300;
    private const int MaxName = 40;
    private const int MaxKey = 256;
    public static readonly string[] LimitKeys = ["cpu", "gpu", "gpuHot", "dimm"];

    public string Unit { get; set; } = "C";
    public string Theme { get; set; } = "green";
    public string Palette { get; set; } = "standard";
    public Dictionary<string, string> Names { get; set; } = new();
    public List<string> Hidden { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads a message from the page. Anything unexpected is dropped: wrong type, too large,
    /// malformed, and values outside what the page itself allows.
    /// </summary>
    public static PageSettings? FromMessage(string json)
    {
        if (json.Length > MaxMessage) return null;
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    type.GetString() != "settings")
                    return null;
            }
            return Sanitize(JsonSerializer.Deserialize<PageSettings>(json, Json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PageSettings Sanitize(PageSettings? page)
    {
        var clean = new PageSettings();
        if (page is null) return clean;
        clean.Unit = page.Unit == "F" ? "F" : "C";
        clean.Theme = page.Theme is "black" or "blue" or "white" ? page.Theme : "green";
        clean.Palette = page.Palette == "cb" ? "cb" : "standard";
        foreach (var (id, name) in (page.Names ?? new()).Take(MaxEntries))
        {
            var trimmed = name?.Trim() ?? "";
            if (id.Length is > 0 and <= MaxKey && trimmed.Length > 0)
                clean.Names[id] = trimmed.Length > MaxName ? trimmed[..MaxName] : trimmed;
        }
        clean.Hidden = (page.Hidden ?? new())
            .Where(k => k is { Length: > 0 and <= MaxKey }).Distinct().Take(MaxEntries).ToList();
        var a = page.Alerts ?? new();
        clean.Alerts = new AlertSettings { Temps = a.Temps, Fans = a.Fans, Space = a.Space, Wear = a.Wear };
        foreach (var key in LimitKeys)
        {
            if (a.Limits?.TryGetValue(key, out var l) == true && l is not null &&
                double.IsFinite(l.Warn) && double.IsFinite(l.Crit) && l.Warn >= 20 && l.Crit <= 150 && l.Warn < l.Crit)
                clean.Alerts.Limits[key] = new Limit(l.Warn, l.Crit);
        }
        return clean;
    }
}

internal sealed class AlertSettings
{
    public bool Temps { get; set; } = true;
    public bool Fans { get; set; } = true;
    public bool Space { get; set; } = true;
    public bool Wear { get; set; } = true;
    public Dictionary<string, Limit> Limits { get; set; } = new();
}

/// <summary>Warning and critical temperatures, in °C.</summary>
internal sealed record Limit(double Warn, double Crit);
