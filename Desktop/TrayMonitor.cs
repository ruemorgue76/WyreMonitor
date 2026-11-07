using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// Works for the icon next to the clock while the window may be closed: every few seconds it
/// asks the service for a reading, writes the CPU temperature into the icon and raises a Windows
/// notification when something new needs attention. Off unless the user turns one of them on.
/// </summary>
internal sealed class TrayMonitor : IDisposable
{
    // The service reads at its idle pace (5 s) for these requests: checking faster gains nothing.
    private const int IntervalMs = 5000;
    // The service is looked up again now and then: its port could change after a restart.
    private const int RecheckTicks = 12;
    private static readonly TimeSpan Forget = TimeSpan.FromSeconds(60);

    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon;
    private readonly AppSettings _settings;
    private readonly Action _openWindow;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IntervalMs };
    private readonly AlertRules.FanWatch _fans = new();
    private readonly Dictionary<string, (int Severity, DateTime Seen)> _active = new();
    private string? _url;
    private int _ticks;
    private bool _busy;
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

    /// <summary>Applies the current settings: starts or stops checking, and resets the icon.</summary>
    public void Refresh()
    {
        var on = _settings.TrayTemperature || _settings.Notifications;
        _timer.Enabled = on;
        if (!_settings.TrayTemperature) ShowAppIcon(BaseTooltip);
        if (!_settings.Notifications) _active.Clear();
        if (on) _ = TickAsync();
    }

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (++_ticks % RecheckTicks == 0) _url = null;
            _url ??= await ServiceLink.FindUrlAsync();
            if (_url is null) { NoData(); return; }

            string json;
            try { json = await ServiceLink.GetStringAsync($"{_url}/api/sensors?background=1"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _url = null; NoData(); return; }

            using var doc = JsonDocument.Parse(json);
            var reading = AlertRules.Read(doc.RootElement);
            if (!reading.Ready) return;
            var page = _settings.Page;
            if (_settings.TrayTemperature) ShowTemperature(reading, page);
            if (_settings.Notifications) Notify(AlertRules.Evaluate(reading, page, _fans));
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
        if (_settings.TrayTemperature) ShowAppIcon($"{BaseTooltip}\nThe service isn't responding");
    }

    // ---- Temperature in the icon -------------------------------------------------------
    private void ShowTemperature(AlertRules.Reading reading, PageSettings page)
    {
        if (reading.CpuTemp is not { } c || !reading.Elevated) { ShowAppIcon(BaseTooltip); return; }
        var limit = AlertRules.CpuLimit(reading, page);
        var shown = page.Unit == "F" ? c * 9 / 5 + 32 : c;
        var text = Math.Round(shown).ToString("0");
        var colour = c >= limit.Crit ? Color.FromArgb(255, 92, 51) : c >= limit.Warn ? Color.FromArgb(242, 169, 59) : Color.White;
        var key = $"{text}|{colour.ToArgb()}";
        _tray.Text = Truncate($"{BaseTooltip}\nCPU {AlertRules.FormatTemp(c, page)}");
        if (key == _iconKey) return;

        var next = DrawNumber(text, colour);
        _tray.Icon = next;
        _numberIcon?.Dispose();
        _numberIcon = next;
        _iconKey = key;
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

    /// <summary>The number drawn on the board's green, sized for the tray at the current scaling.</summary>
    internal static Icon DrawNumber(string text, Color colour)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(18, 58, 50));
            using var path = RoundedRect(new RectangleF(0, 0, size.Width, size.Height), size.Width / 6f);
            g.FillPath(background, path);

            // As large as fits: two digits fill the height, three are narrower.
            var em = size.Height * 0.8f;
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,   // three digits must stay on one line
            };
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                if (g.MeasureString(text, font, PointF.Empty, format).Width <= size.Width || em < 6) break;
                font.Dispose();
                em -= 0.5f;
            }
            using (font)
            using (var brush = new SolidBrush(colour))
                g.DrawString(text, font, brush, new RectangleF(0, size.Height * 0.04f, size.Width, size.Height), format);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Notifications ---------------------------------------------------------------------
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
