using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WyreMonitor.Desktop;

/// <summary>
/// The app window: the page served by the WyreMonitor service, shown in WebView2 (the Edge
/// engine built into Windows). It runs with the user's normal rights and can show nothing but
/// that page: other addresses, new windows and developer tools are blocked.
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 3000 };
    private readonly Action<PageSettings> _onPageSettings;
    private readonly Action _onExport;
    private readonly Action _onMini;
    private string? _url;
    private Themes.Theme _theme;

    public MainWindow(Icon icon, Action<PageSettings> onPageSettings, Action onExport, Action onMini, string? theme)
    {
        _onMini = onMini;
        _onPageSettings = onPageSettings;
        _onExport = onExport;
        Text = "WyreMonitor";
        Icon = icon;
        // The page's background from the first frame, so opening never flashes another colour.
        _theme = Themes.For(theme);
        BackColor = _web.DefaultBackgroundColor = _theme.Page;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 950);
        Size = new Size(Math.Min(1680, screen.Width - 40), Math.Min(1000, screen.Height - 40));
        WindowPlacement.Restore(this);

        Controls.Add(_web);
        _retry.Tick += async (_, _) => await ConnectAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TaskbarButton.Apply(Handle);
        PaintTitleBar();
    }

    /// <summary>The board theme chosen in the page: background and title bar follow it.</summary>
    public void ApplyTheme(string? theme)
    {
        var next = Themes.For(theme);
        if (next == _theme) return;
        _theme = next;
        BackColor = _web.DefaultBackgroundColor = _theme.Page;
        Icon = AppIcon.For(theme);
        PaintTitleBar();
    }

    // Title bar in the page colours, also when Windows paints title bars with the accent
    // colour. On older Windows these calls just fail, with no consequences.
    private void PaintTitleBar()
    {
        if (!IsHandleCreated) return;
        var dark = _theme.Light ? 0 : 1;
        _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var caption = ColorRef(_theme.Page);
        _ = DwmSetWindowAttribute(Handle, DwmCaptionColor, ref caption, sizeof(int));
        var text = ColorRef(_theme.Text);
        _ = DwmSetWindowAttribute(Handle, DwmTextColor, ref text, sizeof(int));
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // A minimized window hides the page: WebView2 then pauses its animations and slows
        // its timers, and the sensors go back to their idle pace.
        _web.Visible = WindowState != FormWindowState.Minimized;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppInfo.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            // The page sends three kinds of message: a copy of its settings for the icon next to
            // the clock, "export" (the CSV) and "mini" (switch to the small window). Accepted only from the service's page,
            // size-checked and validated.
            settings.IsWebMessageEnabled = true;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            // Only the service's page, and the "not running" message the app writes itself.
            core.NavigationStarting += (_, args) =>
            {
                var allowed = (_url is not null && args.Uri.StartsWith(_url + "/", StringComparison.OrdinalIgnoreCase))
                    || args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)
                    || args.Uri == "about:blank";
                if (!allowed) args.Cancel = true;
            };
            core.WebMessageReceived += (_, args) =>
            {
                if (_url is null || !args.Source.StartsWith(_url + "/", StringComparison.OrdinalIgnoreCase)) return;
                string json;
                try { json = args.WebMessageAsJson; } catch (ArgumentException) { return; }
                if (json == """{"type":"export"}""") _onExport();
                else if (json == """{"type":"mini"}""") _onMini();
                else if (PageSettings.FromMessage(json) is { } page) _onPageSettings(page);
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;

            await ConnectAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "The window needs Microsoft Edge WebView2, which is missing.\n\n" +
                "Download it from https://developer.microsoft.com/microsoft-edge/webview2/ and open WyreMonitor again.",
                "WyreMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex) when (!IsDisposed)
        {
            AppInfo.LogError(ex);
            MessageBox.Show(this, $"The window could not open.\n\n{ex.Message}", "WyreMonitor",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>Shows the page, or a message while the service is not running yet.</summary>
    private async Task ConnectAsync()
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _retry.Stop();

        var url = await ServiceLink.FindUrlAsync();
        if (IsDisposed) return;

        if (url is null)
        {
            _url = null;
            _web.CoreWebView2.NavigateToString(ServiceMissingPage(_theme));
            _retry.Start();
            return;
        }

        _url = url;
        _web.CoreWebView2.Navigate($"{url}/");
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string ServiceMissingPage(Themes.Theme t) => $"""
        <!doctype html><html lang="en"><meta charset="utf-8"><title>WyreMonitor</title>
        <body style="margin:0;background:{Hex(t.Page)};color:{Hex(t.Text)};font:16px/1.5 'Segoe UI',sans-serif">
        <div style="padding:48px;max-width:640px">
        <h1 style="font-size:28px;margin:0 0 8px">Waiting for the WyreMonitor service</h1>
        <p style="color:{Hex(t.TextDim)};margin:0">The service that reads the sensors isn't running yet. Right after Windows
        starts this can take a few seconds. If this page stays, open Services, find WyreMonitor and start it,
        or reinstall WyreMonitor.</p></div></body></html>
        """;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) WindowPlacement.Save(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _retry.Dispose();
        _web.Dispose();
        // Disposing a Form inside its own close handler is unsafe: do it right after.
        if (IsHandleCreated) BeginInvoke(Dispose);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
