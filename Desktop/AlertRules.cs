using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WyreMonitor.Desktop;

/// <summary>
/// The alerts of the page (collectAlerts in wwwroot/app.js), for Windows notifications while the
/// window is closed. Same limits, same parts, same names: change both together.
/// </summary>
internal static partial class AlertRules
{
    public sealed record Sensor(string Id, string Name, string Type, double Value);
    public sealed record Group(string Name, string Type, List<Sensor> Sensors);
    public sealed record Reading(bool Ready, bool Elevated, double? CpuTemp, string? SystemDrive, List<Group> Hardware, HashSet<string> SignalledFans);
    public sealed record Alert(string Key, int Severity, string Title, string Detail);

    /// <summary>How many checks in a row each fan read 0 RPM.</summary>
    public sealed class FanWatch
    {
        internal readonly Dictionary<string, int> Zeros = new();
    }

    private static readonly Limit CpuAmd = new(90, 95);
    private static readonly Limit CpuIntel = new(95, 100);
    private static readonly Dictionary<string, Limit> Defaults = new()
    {
        ["gpu"] = new(83, 88), ["gpuHot"] = new(95, 105), ["dimm"] = new(60, 75),
        ["nvme"] = new(70, 80), ["ssd"] = new(60, 70), ["hdd"] = new(50, 55),
    };
    private const double FreeWarn = 0.05, FreeCrit = 0.01, LifeWarn = 20, LifeCrit = 10;

    [GeneratedRegex("Resolution|Limit|^Warning Temperature$|^Critical Temperature$")]
    private static partial Regex NotAReading();
    [GeneratedRegex(@"\(#(\d+)\)$")]
    private static partial Regex ModuleNumber();

    private static bool IsRealTemp(Sensor s) => s.Type == "Temperature" && !NotAReading().IsMatch(s.Name);

    public static Reading Read(JsonElement root)
    {
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        static bool Bool(JsonElement e, string name, bool fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

        var groups = new List<Group>();
        if (root.TryGetProperty("hardware", out var hw) && hw.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in hw.EnumerateArray())
            {
                var sensors = new List<Sensor>();
                if (g.TryGetProperty("sensors", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var s in list.EnumerateArray())
                        sensors.Add(new Sensor(Str(s, "id"), Str(s, "name"), Str(s, "type"), Num(s, "value") ?? 0));
                groups.Add(new Group(Str(g, "name"), Str(g, "type"), sensors));
            }
        }
        double? cpuTemp = root.TryGetProperty("summary", out var summary) ? Num(summary, "cpuTemp") : null;
        var drive = root.TryGetProperty("systemDrive", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
        // Headers that have had a signal (docs/design/fans.md): an empty header never alerts.
        var signalled = new HashSet<string>();
        if (root.TryGetProperty("fanSignals", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
                if (Str(f, "id") is { Length: > 0 } id) signalled.Add(id);
        return new Reading(Bool(root, "ready", true), Bool(root, "elevated", false), cpuTemp, drive, groups, signalled);
    }

    public static Limit CpuLimit(Reading r, PageSettings page)
    {
        if (page.Alerts.Limits.TryGetValue("cpu", out var custom)) return custom;
        var cpu = r.Hardware.FirstOrDefault(h => h.Type == "Cpu");
        return cpu?.Name.Contains("intel", StringComparison.OrdinalIgnoreCase) == true ? CpuIntel : CpuAmd;
    }

    public static string FormatTemp(double celsius, PageSettings page) =>
        page.Unit == "F"
            ? $"{Math.Round(celsius * 9 / 5 + 32).ToString(CultureInfo.CurrentCulture)} °F"
            : $"{Math.Round(celsius).ToString(CultureInfo.CurrentCulture)} °C";

    public static List<Alert> Evaluate(Reading r, PageSettings page, FanWatch fanWatch)
    {
        var alerts = new List<Alert>();
        if (!r.Ready || !r.Elevated) return alerts;
        var on = page.Alerts;
        var hidden = page.Hidden.ToHashSet();
        bool Shown(string key) => !hidden.Contains(key);
        Limit LimitFor(string key) => on.Limits.TryGetValue(key, out var l) ? l : Defaults[key];
        string T(double c) => FormatTemp(c, page);

        void CheckTemp(string key, double? value, Limit limit, string label)
        {
            if (value is not { } v || !on.Temps) return;
            const string act = "Check its cooling, or change the limit in Settings.";
            if (v >= limit.Crit) alerts.Add(new(key, 2, $"{label} at {T(v)}", $"Above the critical limit of {T(limit.Crit)}. {act}"));
            else if (v >= limit.Warn) alerts.Add(new(key, 1, $"{label} at {T(v)}", $"Above the warning threshold of {T(limit.Warn)}. {act}"));
        }

        var cpu = r.Hardware.FirstOrDefault(h => h.Type == "Cpu");
        if (cpu is not null && Shown(cpu.Name)) CheckTemp("temp:cpu", r.CpuTemp, CpuLimit(r, page), "CPU");

        var gpus = r.Hardware.Where(h => h.Type.StartsWith("Gpu", StringComparison.Ordinal))
            .OrderBy(h => h.Type is "GpuNvidia" or "GpuAmd" ? 0 : 1).ToList();
        foreach (var g in gpus.Where(g => Shown(g.Name)))
        {
            var temps = g.Sensors.Where(IsRealTemp).ToList();
            var temp = temps.FirstOrDefault(s => s.Name == "GPU Core")
                ?? temps.FirstOrDefault(s => s.Name == "GPU" || s.Name.Contains("Edge"))
                ?? temps.FirstOrDefault(s => !s.Name.Contains("Hot Spot") && !s.Name.Contains("Memory"));
            var hot = temps.FirstOrDefault(s => s.Name.Contains("Hot Spot"));
            var name = gpus.Count > 1 ? ShortGpuName(g.Name) : "GPU";
            CheckTemp($"temp:{g.Name}", temp?.Value, LimitFor("gpu"), name);
            CheckTemp($"hot:{g.Name}", hot?.Value, LimitFor("gpuHot"), $"{name} hot spot");
        }

        foreach (var g in r.Hardware.Where(h => h.Type == "Memory" && ModuleNumber().IsMatch(h.Name) && Shown(h.Name)))
        {
            var t = g.Sensors.FirstOrDefault(s => IsRealTemp(s) && s.Name.StartsWith("DIMM", StringComparison.Ordinal));
            CheckTemp($"temp:{g.Name}", t?.Value, LimitFor("dimm"), $"Memory module #{ModuleNumber().Match(g.Name).Groups[1].Value}");
        }

        foreach (var g in r.Hardware.Where(h => h.Type == "Storage" && Shown(h.Name)))
        {
            Sensor? Find(string type, string name) => g.Sensors.FirstOrDefault(s => s.Type == type && s.Name == name);
            var total = Find("Data", "Total Space");
            if (total is null || total.Value == 0) continue;   // empty card reader
            var temps = g.Sensors.Where(IsRealTemp).ToList();
            var life = Find("Level", "Life");
            var kind = temps.Count > 1 || g.Name.Contains("nvme", StringComparison.OrdinalIgnoreCase) ? "nvme" : life is not null ? "ssd" : "hdd";
            var warn = Find("Temperature", "Warning Temperature")?.Value is > 0 and var w ? w : Defaults[kind].Warn;
            var crit = Find("Temperature", "Critical Temperature")?.Value is > 0 and var c ? c : Defaults[kind].Crit;
            var temp = Find("Temperature", "Temperature") ?? temps.FirstOrDefault();
            var shortName = ShortDriveName(g.Name);
            CheckTemp($"temp:{g.Name}", temp?.Value, new Limit(warn, crit), $"Drive {shortName}");

            var isSystem = !string.IsNullOrEmpty(r.SystemDrive) &&
                string.Equals(g.Name.Trim(), r.SystemDrive.Trim(), StringComparison.OrdinalIgnoreCase);
            var free = Find("Data", "Free Space");
            if (on.Space && isSystem && free is not null && total.Value > 0)
            {
                var ratio = free.Value / total.Value;
                if (ratio < FreeWarn)
                    alerts.Add(new($"space:{g.Name}", ratio < FreeCrit ? 2 : 1, "Windows drive almost full",
                        $"{FormatBytes(free.Value)} free of {FormatBytes(total.Value)}. Free some space: Windows slows down and updates fail when it runs out."));
            }
            if (on.Wear && life is not null && life.Value <= LifeWarn)
                alerts.Add(new($"wear:{g.Name}", life.Value <= LifeCrit ? 2 : 1, $"SSD {shortName} near end of life",
                    $"Remaining life {Math.Round(life.Value)} %. Back up its data and plan a replacement."));
        }

        // Fans by controller, labelled as on the board: A = first chip, B = second.
        var boards = r.Hardware.Where(h => (h.Type is "SuperIO" or "EmbeddedController") &&
            h.Sensors.Any(s => IsRealTemp(s) || s.Type == "Fan")).ToList();
        for (var c = 0; c < boards.Count; c++)
        {
            var fans = boards[c].Sensors.Where(s => s.Type == "Fan").ToList();
            for (var i = 0; i < fans.Count; i++)
            {
                var f = fans[i];
                // Only fans the user asked to keep an eye on: 0 RPM can't tell a stopped fan from an
                // empty header. Two checks in a row: one odd reading is not a stop.
                var zeros = f.Value <= 0 ? fanWatch.Zeros.GetValueOrDefault(f.Id) + 1 : 0;
                fanWatch.Zeros[f.Id] = zeros;
                if (!on.Fans || !Shown(f.Id) || !page.WatchFans.Contains(f.Id) || !r.SignalledFans.Contains(f.Id) || zeros < 2) continue;
                var name = page.Names.TryGetValue(f.Id, out var custom) ? custom : $"Fan {(char)('A' + c)}{i + 1}";
                alerts.Add(new($"fan:{f.Id}", 2, $"{name} stopped", "It reads 0 RPM. Check its cable and that nothing blocks it."));
            }
        }
        return alerts.OrderByDescending(a => a.Severity).ToList();
    }

    private static string ShortGpuName(string name) =>
        Regex.Replace(Regex.Replace(name, "^(NVIDIA|AMD|Intel\\(R\\)) ", ""), "^GeForce ", "")
            .Replace("Radeon(TM) ", "Radeon ").Replace("(R)", "").Replace("(TM)", "").Trim();

    private static string ShortDriveName(string name) =>
        Regex.Replace(Regex.Replace(name, "^Samsung SSD ", ""), @"^WDC (WD\w+)-\w+$", "$1");

    private static string FormatBytes(double gb) =>
        gb >= 1000 ? $"{Math.Round(gb / 1000, 2).ToString(CultureInfo.CurrentCulture)} TB"
                   : $"{Math.Round(gb, gb < 100 ? 1 : 0).ToString(CultureInfo.CurrentCulture)} GB";
}
