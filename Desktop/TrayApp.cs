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
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TrayMonitor _monitor;
    private readonly ToolStripMenuItem _miniItem;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60 * 60 * 1000 };
    private MainWindow? _window;
    private MiniWindow? _mini;
    private bool _closeHintShown;

    public TrayApp(bool showWindow, bool showMini = false)
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

        var temperature = new ToolStripMenuItem("Show the CPU temperature on this icon") { Checked = _settings.TrayTemperature };
        temperature.Click += (_, _) =>
        {
            _settings.TrayTemperature = temperature.Checked = !temperature.Checked;
            _settings.Save();
            _monitor!.Refresh();
        };

        var notifications = new ToolStripMenuItem("Notify me when something needs attention") { Checked = _settings.Notifications };
        notifications.Click += (_, _) =>
        {
            _settings.Notifications = notifications.Checked = !notifications.Checked;
            _settings.Save();
            _monitor!.Refresh();
        };

        _miniItem = new ToolStripMenuItem("Small window, always on top") { Checked = _settings.MiniWindow };
        _miniItem.Click += (_, _) => SetMini(!_miniItem.Checked);

        var checkNow = new ToolStripMenuItem("Check for updates…");
        checkNow.Click += async (_, _) => await UpdateCheck.CheckAndTellAsync();
        var autoCheck = new ToolStripMenuItem("Check for updates automatically") { Checked = _settings.AutoUpdateCheck };
        autoCheck.Click += async (_, _) =>
        {
            _settings.AutoUpdateCheck = autoCheck.Checked = !autoCheck.Checked;
            _settings.Save();
            if (_settings.AutoUpdateCheck) await AutoCheckAsync();
        };

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Quit();

        var version = new ToolStripMenuItem($"WyreMonitor {AppInfo.Version}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            version, new ToolStripSeparator(), open, _miniItem, new ToolStripSeparator(),
            autostart, temperature, notifications, new ToolStripSeparator(),
            checkNow, autoCheck, new ToolStripSeparator(), exit,
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

        // A click on a notification opens the window.
        _tray.BalloonTipClicked += (_, _) => ShowOnUiThread();

        _monitor = new TrayMonitor(_tray, _icon, _settings, ShowWindow);
        _monitor.Refresh();

        // Once a day at most, only if the user turned it on; the hourly timer just looks at the date.
        _updateTimer.Tick += async (_, _) => await AutoCheckAsync();
        _updateTimer.Start();
        _ = AutoCheckAsync();

        if (showMini) OpenMini(remember: false);
        else if (_settings.MiniWindow) SetMini(true);
        if (showWindow) ShowOnUiThread();
    }

    private void SetMini(bool open)
    {
        _settings.MiniWindow = _miniItem.Checked = open;
        _settings.Save();
        if (open) OpenMini(remember: true);
        else _mini?.Close();
    }

    /// <param name="remember">False for a one-off --mini run: nothing is saved about it.</param>
    private void OpenMini(bool remember)
    {
        if (_mini is not null) return;
        _mini = new MiniWindow(_icon, _settings, ShowWindow, remember);
        _mini.FormClosed += (_, _) =>
        {
            _mini = null;
            // Closed with its own ×: it stays closed next time too. Not when quitting the app.
            if (remember && !_quitting) { _settings.MiniWindow = _miniItem.Checked = false; _settings.Save(); }
            else _miniItem.Checked = _settings.MiniWindow;
        };
        _miniItem.Checked = true;
        _mini.Show();
    }

    private async Task AutoCheckAsync()
    {
        if (!_settings.AutoUpdateCheck || DateTime.UtcNow - _settings.LastUpdateCheck < UpdateCheck.Every) return;
        var result = await UpdateCheck.CheckAsync();
        if (result.Outcome == UpdateCheck.Outcome.Unavailable) return;   // try again at the next tick
        _settings.LastUpdateCheck = DateTime.UtcNow;
        if (result.Outcome == UpdateCheck.Outcome.NewVersion && result.Version != _settings.AnnouncedVersion)
        {
            _settings.AnnouncedVersion = result.Version;
            var page = result.PageUrl!;
            Toasts.Show($"WyreMonitor {result.Version} is available",
                $"You have {AppInfo.Version}. Click to open its download page.",
                () => _uiThread.BeginInvoke(() => UpdateCheck.OpenPage(page)));
        }
        _settings.Save();
    }

    private void Export()
    {
        if (_window is not null) _ = Exporter.ExportAsync(_window, _settings.Page);
    }

    /// <summary>The page sent its settings: the icon uses them for its temperature and alerts.</summary>
    private void OnPageSettings(PageSettings page)
    {
        _settings.Page = page;
        _settings.Save();
        _monitor.Refresh();
    }

    /// <summary>Safe from any thread.</summary>
    public void ShowWindow() => _uiThread.BeginInvoke(ShowOnUiThread);

    private void ShowOnUiThread()
    {
        if (_window is null)
        {
            _window = new MainWindow(_icon, OnPageSettings, Export);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                if (_closeHintShown) return;
                _closeHintShown = true;
                const string title = "WyreMonitor is still running";
                const string text = "You'll find it next to the clock. To quit it: right-click the icon, then Exit.";
                // Shown right after the close, not inside it: Windows refuses a notification
                // created while it is still delivering a synchronous message to this window.
                _uiThread.BeginInvoke(() =>
                {
                    if (!Toasts.Show(title, text, ShowWindow)) _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
                });
            };
            _window.Show();
        }

        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private bool _quitting;

    private void Quit()
    {
        _quitting = true;
        _mini?.Close();
        _closeHintShown = true;
        _window?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateTimer.Dispose();
            _monitor.Dispose();
            _tray.Dispose();
            _uiThread.Dispose();
        }
        base.Dispose(disposing);
    }
}
