using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// A small window that stays above the others with the headline numbers: CPU, graphics card and
/// memory. Drawn natively and fed by /api/summary every 2 seconds, so it costs far less than a
/// second page would.
/// </summary>
internal sealed class MiniWindow : Form
{
    private static readonly Color Background = ColorTranslator.FromHtml("#0c2a25");
    private static readonly Color Dim = ColorTranslator.FromHtml("#a9c4b9");
    private static readonly Color Silk = ColorTranslator.FromHtml("#f0f1e4");

    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };   // the service reads at this pace for it
    private readonly Font _label;
    private readonly Font _value;
    private string? _url;
    private bool _busy;
    private int _ticks;
    private Row[] _rows = [];

    private sealed record Row(string Label, string[] Values, Color[] Colours);

    public MiniWindow(Icon icon, AppSettings settings)
    {
        _settings = settings;
        Text = "WyreMonitor";
        Icon = icon;
        TopMost = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = MinimizeBox = false;
        BackColor = Background;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        _label = new Font("Segoe UI", 9f);
        _value = new Font("Segoe UI", 11f, FontStyle.Bold);
        ClientSize = new Size(LogicalToDeviceUnits(250), LogicalToDeviceUnits(96));

        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Location = settings.MiniX is { } x && settings.MiniY is { } y && Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 20, y + 10))
            ? new Point(x, y)
            : new Point(area.Right - Width - 16, area.Top + 16);

        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar in the page colours, like the main window.
        var on = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        var caption = Background.R | (Background.G << 8) | (Background.B << 16);
        _ = DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        await RefreshAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _settings.MiniX = Location.X;
        _settings.MiniY = Location.Y;
        _settings.Save();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _timer.Dispose();
        _label.Dispose();
        _value.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        try
        {
            if (++_ticks % 30 == 0) _url = null;   // look the service up again now and then
            _url ??= await ServiceLink.FindUrlAsync();
            if (_url is null) { ShowMessage("Waiting for the service"); return; }
            string json;
            try { json = await ServiceLink.GetStringAsync($"{_url}/api/summary"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _url = null; ShowMessage("Waiting for the service"); return; }
            if (IsDisposed) return;
            ApplySummary(json);
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

    private void ShowMessage(string message)
    {
        _rows = [new Row(message, [], [])];
        Invalidate();
    }

    private void ApplySummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.False) { ShowMessage("Reading sensors"); return; }
        var s = root.GetProperty("summary");
        double? Num(string name) => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var page = _settings.Page;
        var cpuName = root.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var cpuLimit = page.Alerts.Limits.TryGetValue("cpu", out var custom) ? custom
            : cpuName?.Contains("intel", StringComparison.OrdinalIgnoreCase) == true ? new Limit(95, 100) : new Limit(90, 95);
        var gpuLimit = page.Alerts.Limits.TryGetValue("gpu", out var g) ? g : new Limit(83, 88);

        string Temp(double? v) => v is { } t ? AlertRules.FormatTemp(t, page) : "n/a";
        static string Pct(double? v) => v is { } p ? $"{Math.Round(p)} %" : "n/a";
        static string Watts(double? v) => v is { } w ? $"{Math.Round(w)} W" : "";
        Color Heat(double? v, Limit l) => v is not { } t ? Dim : t >= l.Crit ? Color.FromArgb(255, 92, 51) : t >= l.Warn ? Color.FromArgb(242, 169, 59) : Silk;

        _rows =
        [
            new Row("CPU", [Temp(Num("cpuTemp")), Pct(Num("cpuLoad")), Watts(Num("cpuPower"))], [Heat(Num("cpuTemp"), cpuLimit), Silk, Silk]),
            new Row("GPU", [Temp(Num("gpuTemp")), Pct(Num("gpuLoad")), Watts(Num("gpuPower"))], [Heat(Num("gpuTemp"), gpuLimit), Silk, Silk]),
            new Row("Memory", [Pct(Num("ramLoad")), Num("ramUsed") is { } used ? $"{used:0.0} GB" : "", ""], [Silk, Silk, Silk]),
        ];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var pad = LogicalToDeviceUnits(10);
        var line = (ClientSize.Height - 2 * pad) / 3f;
        float[] x = [pad, LogicalToDeviceUnits(70), LogicalToDeviceUnits(146), LogicalToDeviceUnits(200)];
        for (var r = 0; r < _rows.Length; r++)
        {
            var y = pad + r * line;
            var row = _rows[r];
            TextRenderer.DrawText(g, row.Label, _label, new Point((int)x[0], (int)(y + 2)), Dim);
            for (var i = 0; i < row.Values.Length; i++)
                TextRenderer.DrawText(g, row.Values[i], _value, new Point((int)x[i + 1], (int)y), row.Colours[i]);
        }
    }
}
