using System.Drawing;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// The app's lifetime: the tray icon stays until "Exit". The window is created when
/// needed and destroyed on close, so the page uses no resources while nobody looks at it.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Control _uiThread = new();
    private readonly Icon _icon;
    private MainWindow? _window;
    private bool _closeHintShown;

    public TrayApp(bool showWindow)
    {
        _ = _uiThread.Handle;   // forces creation, so BeginInvoke works from other threads
        _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

        var open = new ToolStripMenuItem("Open WyreMonitor") { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        open.Click += (_, _) => ShowOnUiThread();

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = ServiceLink.AutostartEnabled };
        autostart.Click += (_, _) =>
        {
            try
            {
                ServiceLink.SetAutostart(!autostart.Checked);
                autostart.Checked = ServiceLink.AutostartEnabled;
            }
            catch (Exception ex)
            {
                AppInfo.LogError(ex);
                MessageBox.Show("Couldn't change the start with Windows setting.", "WyreMonitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Quit();

        var version = new ToolStripMenuItem($"WyreMonitor {AppInfo.Version}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            version, new ToolStripSeparator(), open, autostart, new ToolStripSeparator(), exit,
        });

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = $"WyreMonitor {AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowOnUiThread();
        };

        if (showWindow) ShowOnUiThread();
    }

    /// <summary>Safe from any thread.</summary>
    public void ShowWindow() => _uiThread.BeginInvoke(ShowOnUiThread);

    private void ShowOnUiThread()
    {
        if (_window is null)
        {
            _window = new MainWindow(_icon);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                if (_closeHintShown) return;
                _closeHintShown = true;
                _tray.ShowBalloonTip(5000, "WyreMonitor is still running",
                    "You'll find it here, next to the clock. To quit it: right-click the icon, then Exit.",
                    ToolTipIcon.Info);
            };
            _window.Show();
        }

        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _closeHintShown = true;
        _window?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _uiThread.Dispose();
        }
        base.Dispose(disposing);
    }
}
