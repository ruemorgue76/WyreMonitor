using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// Works for the icon next to the clock. Two independent jobs, each off unless the user turns it on:
/// <list type="bullet">
/// <item>the reading chosen in the icon's menu, drawn in the icon every second, but only while
/// neither window is open (they already show it);</item>
/// <item>Windows notifications when something new needs attention, checked every 5 seconds
/// whatever is open.</item>
/// </list>
/// </summary>
internal sealed class TrayMonitor : IDisposable
{
    private const int NotifyEvery = 5;     // ticks (seconds) between alert checks
    private const int RecheckTicks = 60;   // the service is looked up again now and then (its port may change)
    private static readonly TimeSpan Forget = TimeSpan.FromSeconds(60);

    // Style: the board of the chosen theme, with a strip at the bottom in the part's colour; a
    // percentage draws the strip as long as its value. Light themes take darker strips.
    private static Color PartColour(string part, bool light) => (part, light) switch
    {
        ("cpu", false) => Color.FromArgb(216, 180, 90),
        ("gpu", false) => Color.FromArgb(110, 176, 255),
        ("memory", false) => Color.FromArgb(196, 140, 255),
        ("cpu", true) => Color.FromArgb(154, 115, 24),
        ("gpu", true) => Color.FromArgb(37, 99, 186),
        _ => Color.FromArgb(128, 70, 196),
    };

    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon;
    private readonly AppSettings _settings;
    private readonly Action _openWindow;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly AlertRules.FanWatch _fans = new();
    private readonly Dictionary<string, (int Severity, DateTime Seen)> _active = new();
    private string? _url;
    private int _ticks;
    private bool _busy;
    private bool _windowsOpen;
    private Icon? _numberIcon;
    private string _iconKey = "";

    public TrayMonitor(NotifyIcon tray, Icon appIcon, AppSettings settings, Action openWindow)
    {
        _openWindow = openWindow;
        _tray = tray;
        _appIcon = appIcon;
        _settings = settings;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private string BaseTooltip => $"{AppInfo.Name} {AppInfo.Version}";

    private TrayValues.Value? Shown => _windowsOpen ? null : TrayValues.Find(_settings.TrayValue);

    /// <summary>The main or small window is open: the icon goes back to the plain one.</summary>
    public bool WindowsOpen
    {
        set
        {
            if (_windowsOpen == value) return;
            _windowsOpen = value;
            Refresh();
        }
    }

    /// <summary>Applies the current settings: starts or stops checking, and resets the icon.</summary>
    public void Refresh()
    {
        var on = _settings.TrayValue is not null || _settings.Notifications;
        _timer.Enabled = on;
        if (Shown is null) ShowAppIcon(BaseTooltip);
        if (!_settings.Notifications) _active.Clear();
        if (on) _ = TickAsync();
    }

    private async Task TickAsync()
    {
        var shown = Shown;
        var notify = _settings.Notifications && _ticks % NotifyEvery == 0;
        _ticks++;
        if (_busy || (shown is null && !notify)) return;
        _busy = true;
        try
        {
            if (_ticks % RecheckTicks == 0) _url = null;
            _url ??= await ServiceLink.FindUrlAsync();
            if (_url is null) { NoData(); return; }
            try
            {
                // Only the part shown is read every second by the service (?part=).
                if (shown is not null) ShowValue(shown, await ServiceLink.GetStringAsync($"{_url}/api/summary?part={shown.Part}"));
                if (notify) await CheckAlertsAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _url = null; NoData(); }
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void NoData()
    {
        if (Shown is not null) ShowAppIcon($"{BaseTooltip}\nThe service isn't responding");
    }

    // ---- The reading in the icon --------------------------------------------------------------
    private void ShowValue(TrayValues.Value value, string json)
    {
        if (Shown != value) return;   // a window opened, or the choice changed, while asking
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.False) return;
        if (!root.TryGetProperty("summary", out var summary) || !summary.TryGetProperty(value.Field, out var v) ||
            v.ValueKind != JsonValueKind.Number)
        {
            ShowAppIcon($"{BaseTooltip}\n{value.Label}: not available");
            return;
        }

        var reading = v.GetDouble();
        var page = _settings.Page;
        var theme = Themes.For(page.Theme);
        var (warn, crit) = Themes.Alerts(page.Palette, theme.Light);
        string text, detail;
        var colour = theme.Text;
        double? fill = null;
        switch (value.Kind)
        {
            case TrayValues.Kind.Temperature:
                text = Math.Round(page.Unit == "F" ? reading * 9 / 5 + 32 : reading).ToString("0", CultureInfo.InvariantCulture);
                detail = AlertRules.FormatTemp(reading, page);
                var cpuName = root.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var limit = TemperatureLimit(value.Part, cpuName, page);
                colour = reading >= limit.Crit ? crit : reading >= limit.Warn ? warn : theme.Text;
                break;
            case TrayValues.Kind.Percent:
                text = Math.Round(reading).ToString("0", CultureInfo.InvariantCulture);
                detail = $"{text} %";
                fill = Math.Clamp(reading / 100, 0, 1);
                break;
            default:
                text = Math.Round(reading).ToString("0", CultureInfo.InvariantCulture);
                detail = $"{text} W";
                break;
        }

        _tray.Text = Truncate($"{BaseTooltip}\n{value.Label} {detail}");
        var size = SystemInformation.SmallIconSize;
        var stripWidth = fill is { } f ? Math.Max(2, (int)Math.Round(size.Width * f)) : size.Width;
        var key = $"{text}|{colour.ToArgb()}|{value.Part}|{stripWidth}|{page.Theme}";
        if (key == _iconKey) return;

        var next = Draw(text, colour, PartColour(value.Part, theme.Light), fill, theme.Board);
        _tray.Icon = next;
        _numberIcon?.Dispose();
        _numberIcon = next;
        _iconKey = key;
    }

    /// <summary>The same limits as the page: the user's, or the part's own.</summary>
    private static Limit TemperatureLimit(string part, string? cpuName, PageSettings page)
    {
        var key = part == "cpu" ? "cpu" : "gpu";
        if (page.Alerts.Limits.TryGetValue(key, out var custom)) return custom;
        if (part != "cpu") return new Limit(83, 88);
        return cpuName?.Contains("intel", StringComparison.OrdinalIgnoreCase) == true ? new Limit(95, 100) : new Limit(90, 95);
    }

    private void ShowAppIcon(string tooltip)
    {
        _tray.Text = Truncate(tooltip);
        if (_numberIcon is null) return;
        _tray.Icon = _appIcon;
        _numberIcon.Dispose();
        _numberIcon = null;
        _iconKey = "";
    }

    private static string Truncate(string s) => s.Length > 127 ? s[..127] : s;

    /// <summary>The number on the board's green, with the part's strip, sized for the tray.</summary>
    internal static Icon Draw(string text, Color colour, Color strip, double? fill, Color board)
    {
        var size = SystemInformation.SmallIconSize;
        int w = size.Width, h = size.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(board);
            var stripHeight = Math.Max(2, (int)Math.Round(h / 8.0));
            using (var dim = new SolidBrush(Color.FromArgb(70, strip))) g.FillRectangle(dim, 0, h - stripHeight, w, stripHeight);
            var stripWidth = fill is { } f ? Math.Max(2, (int)Math.Round(w * f)) : w;
            using (var bright = new SolidBrush(strip)) g.FillRectangle(bright, 0, h - stripHeight, stripWidth, stripHeight);

            // As large as fits above the strip: two digits fill it, three are narrower.
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            var em = h * 0.8f;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                if (g.MeasureString(text, font, PointF.Empty, format).Width <= w || em < 6) break;
                font.Dispose();
                em -= 0.5f;
            }
            using (font)
            using (var brush = new SolidBrush(colour))
                g.DrawString(text, font, brush, new RectangleF(0, -h * 0.04f, w, h - stripHeight / 2f), format);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    // ---- Notifications ---------------------------------------------------------------------
    private async Task CheckAlertsAsync()
    {
        // The full reading without history: the service keeps reading every part every 5 s.
        var json = await ServiceLink.GetStringAsync($"{_url}/api/sensors?background=1");
        using var doc = JsonDocument.Parse(json);
        var reading = AlertRules.Read(doc.RootElement);
        if (reading.Ready) Notify(AlertRules.Evaluate(reading, _settings.Page, _fans));
    }

    /// <summary>
    /// Announces alerts that are new or got worse. One that clears for a minute is forgotten, so
    /// it is announced again if it comes back; one that stays is announced once.
    /// </summary>
    private void Notify(List<AlertRules.Alert> alerts)
    {
        var now = DateTime.UtcNow;
        var fresh = alerts.Where(a => !_active.TryGetValue(a.Key, out var seen) || a.Severity > seen.Severity).ToList();
        foreach (var a in alerts) _active[a.Key] = (a.Severity, now);
        foreach (var key in _active.Where(p => now - p.Value.Seen > Forget).Select(p => p.Key).ToList()) _active.Remove(key);
        if (fresh.Count == 0) return;

        var title = fresh.Count == 1 ? fresh[0].Title : $"{fresh.Count} things to check";
        var text = fresh.Count == 1 ? fresh[0].Detail : string.Join("\n", fresh.Select(a => a.Title));
        if (text.Length > 250) text = text[..250];
        if (!Toasts.Show(title, text, _openWindow))
            _tray.ShowBalloonTip(10000, title, text, fresh.Any(a => a.Severity == 2) ? ToolTipIcon.Error : ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _numberIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyIcon(IntPtr handle);
}
