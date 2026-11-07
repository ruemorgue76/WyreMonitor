using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WyreMonitor.Desktop;

/// <summary>
/// Windows notifications. They appear under the name and icon of the Start menu shortcut that
/// carries <see cref="AppId"/> (created by the installer). Tray balloons are not used: Windows
/// lets users turn them off (EnableBalloonTips), and then they vanish without a trace.
/// </summary>
internal static class Toasts
{
    public const string AppId = "WyreMonitor.App";

    /// <summary>Ties this process to the shortcut's ID: notifications and taskbar button share it.</summary>
    public static void Register()
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppId); } catch { /* cosmetic */ }
    }

    /// <summary>Shows a notification; false if Windows refused it.</summary>
    public static bool Show(string title, string text, Action onClick)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>" +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) => onClick();
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
