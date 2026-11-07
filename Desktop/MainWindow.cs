using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WyreMonitor.Desktop;

/// <summary>The app window: the dashboard shown in WebView2, the Edge engine built into Windows.</summary>
internal sealed class MainWindow : Form
{
    // Same colour as the page background, so opening never flashes white.
    private static readonly Color Background = ColorTranslator.FromHtml("#0c2a25");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };

    public MainWindow(Icon icon)
    {
        Text = "WyreMonitor";
        Icon = icon;
        BackColor = Background;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 950);
        Size = new Size(Math.Min(1680, screen.Width - 40), Math.Min(1000, screen.Height - 40));

        Controls.Add(_web);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Title bar in the page colours, also when Windows paints title bars with the accent
        // colour. On older Windows these calls just fail, with no consequences.
        var enabled = 1;
        _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        var caption = ColorRef(Background);
        _ = DwmSetWindowAttribute(Handle, DwmCaptionColor, ref caption, sizeof(int));
        var text = ColorRef(ColorTranslator.FromHtml("#f0f1e4"));
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
            // Program Files is not writable: the browser profile lives under the user's data.
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppInfo.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);

            var settings = _web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            _web.Source = new Uri($"{AppInfo.Url}/");
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _web.Dispose();
        BeginInvokeDispose();
    }

    private void BeginInvokeDispose()
    {
        // Disposing a Form inside its own close handler is unsafe: do it right after.
        if (IsHandleCreated) BeginInvoke(Dispose);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
