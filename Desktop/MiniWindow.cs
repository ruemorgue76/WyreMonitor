using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// A small window that stays above the others with the headline numbers: CPU, graphics card and
/// memory. No Windows title bar: it is drawn in the board's colours, dragged from anywhere and
/// closed with its own ×. Fed by /api/summary every 2 seconds and redrawn only when a number
/// changes, so it costs far less than a second page would.
/// </summary>
internal sealed class MiniWindow : Form
{
    private static readonly Color Background = ColorTranslator.FromHtml("#0c2a25");
    private static readonly Color Border = ColorTranslator.FromHtml("#26695a");
    private static readonly Color Hover = ColorTranslator.FromHtml("#123a32");
    private static readonly Color Dim = ColorTranslator.FromHtml("#a9c4b9");
    private static readonly Color Silk = ColorTranslator.FromHtml("#f0f1e4");

    private readonly AppSettings _settings;
    private readonly bool _remember;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };   // the service reads at this pace for it
    private readonly Font _title;
    private readonly Font _label;
    private readonly Font _value;
    private string? _url;
    private bool _busy;
    private int _ticks;
    private Row[] _rows = [];
    private string _shown = "";
    private bool _closeHover;

    private sealed record Row(string Label, string[] Values, Color[] Colours);

    /// <param name="remember">Save where the window is when it closes (not for a one-off --mini).</param>
    public MiniWindow(Icon icon, AppSettings settings, Action openMain, bool remember = true)
    {
        _settings = settings;
        _remember = remember;
        Text = "WyreMonitor";
        Icon = icon;
        TopMost = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Background;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        _title = new Font("Segoe UI", 8f);
        _label = new Font("Segoe UI", 9f);
        _value = new Font("Segoe UI", 11f, FontStyle.Bold);
        ClientSize = new Size(LogicalToDeviceUnits(256), LogicalToDeviceUnits(108));

        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Location = settings.MiniX is { } x && settings.MiniY is { } y && Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 20, y + 10))
            ? new Point(x, y)
            : new Point(area.Right - Width - 16, area.Top + 16);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open WyreMonitor", null, (_, _) => openMain());
        menu.Items.Add("Close", null, (_, _) => Close());
        ContextMenuStrip = menu;

        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    // A soft shadow around the borderless window.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2;   // DWMWCP_ROUND: rounded corners on Windows 11, ignored elsewhere
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        await RefreshAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!_remember) return;
        _settings.MiniX = Location.X;
        _settings.MiniY = Location.Y;
        _settings.Save();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _timer.Dispose();
        _title.Dispose();
        _label.Dispose();
        _value.Dispose();
    }

    // ---- Dragging and the close button --------------------------------------------------
    private Rectangle CloseBox
    {
        get
        {
            var s = LogicalToDeviceUnits(22);
            return new Rectangle(ClientSize.Width - s - LogicalToDeviceUnits(4), LogicalToDeviceUnits(4), s, s);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = CloseBox.Contains(e.Location);
        if (hover == _closeHover) return;
        _closeHover = hover;
        Invalidate(CloseBox);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_closeHover) return;
        _closeHover = false;
        Invalidate(CloseBox);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (CloseBox.Contains(e.Location)) { Close(); return; }
        // Anywhere else: move the window, as if its title bar were grabbed.
        ReleaseCapture();
        _ = SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    // ---- Data ------------------------------------------------------------------------------
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

    private void ShowMessage(string message) => SetRows([new Row(message, [], [])]);

    /// <summary>Redraws only when something shown changed: most readings repeat the last ones.</summary>
    private void SetRows(Row[] rows)
    {
        var key = string.Join("|", rows.Select(r => $"{r.Label}:{string.Join(",", r.Values)}:{string.Join(",", r.Colours.Select(c => c.ToArgb()))}"));
        if (key == _shown) return;
        _shown = key;
        _rows = rows;
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

        SetRows(
        [
            new Row("CPU", [Temp(Num("cpuTemp")), Pct(Num("cpuLoad")), Watts(Num("cpuPower"))], [Heat(Num("cpuTemp"), cpuLimit), Silk, Silk]),
            new Row("GPU", [Temp(Num("gpuTemp")), Pct(Num("gpuLoad")), Watts(Num("gpuPower"))], [Heat(Num("gpuTemp"), gpuLimit), Silk, Silk]),
            new Row("Memory", [Pct(Num("ramLoad")), Num("ramUsed") is { } used ? $"{used:0.0} GB" : "", ""], [Silk, Silk, Silk]),
        ]);
    }

    // ---- Drawing ---------------------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var px = (int v) => LogicalToDeviceUnits(v);

        using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        TextRenderer.DrawText(g, "WyreMonitor", _title, new Point(px(10), px(7)), Dim);

        // The close mark: a thin ×, lighter on hover. No red.
        var box = CloseBox;
        if (_closeHover) using (var b = new SolidBrush(Hover)) g.FillRectangle(b, box);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(_closeHover ? Silk : Dim, Math.Max(1f, px(1) * 1.2f)))
        {
            var inset = box.Width * 0.32f;
            g.DrawLine(pen, box.Left + inset, box.Top + inset, box.Right - inset, box.Bottom - inset);
            g.DrawLine(pen, box.Right - inset, box.Top + inset, box.Left + inset, box.Bottom - inset);
        }
        g.SmoothingMode = SmoothingMode.None;

        var top = px(30);
        var line = (ClientSize.Height - top - px(8)) / 3f;
        int[] x = [px(10), px(70), px(146), px(200)];
        for (var r = 0; r < _rows.Length; r++)
        {
            var y = (int)(top + r * line);
            var row = _rows[r];
            TextRenderer.DrawText(g, row.Label, _label, new Point(x[0], y + px(2)), Dim);
            for (var i = 0; i < row.Values.Length; i++)
                TextRenderer.DrawText(g, row.Values[i], _value, new Point(x[i + 1], y), row.Colours[i]);
        }
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
