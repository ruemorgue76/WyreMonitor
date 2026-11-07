using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// Remembers where the window was and how big, per user, in %LOCALAPPDATA%\WyreMonitor.
/// A saved place that no longer fits any screen (a monitor was unplugged) is ignored.
/// </summary>
internal static class WindowPlacement
{
    private sealed record Saved(int X, int Y, int Width, int Height, bool Maximized);

    private static string FilePath => Path.Combine(AppInfo.DataDir, "window.json");

    public static void Restore(Form form)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved is null) return;

            var bounds = new Rectangle(saved.X, saved.Y,
                Math.Max(saved.Width, form.MinimumSize.Width), Math.Max(saved.Height, form.MinimumSize.Height));
            // The title bar must be on a screen, or the window could not be dragged back.
            var titleBar = new Rectangle(bounds.X + 40, bounds.Y, Math.Max(1, bounds.Width - 80), 30);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(titleBar))) return;

            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
            if (saved.Maximized) form.WindowState = FormWindowState.Maximized;
        }
        catch (Exception ex)
        {
            // A damaged file only costs the saved place: the window opens centred.
            AppInfo.LogError(ex);
        }
    }

    public static void Save(Form form)
    {
        try
        {
            // RestoreBounds is the normal size of a maximized or minimized window.
            var b = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (b.Width <= 0 || b.Height <= 0) return;
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Saved(b.X, b.Y, b.Width, b.Height, form.WindowState == FormWindowState.Maximized)));
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
        }
    }
}
