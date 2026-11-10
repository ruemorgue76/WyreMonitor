using System.Runtime.InteropServices;

namespace WyreMonitor.Desktop;

/// <summary>
/// The taskbar button of a window. The process carries the Start menu shortcut's ID (for the
/// notifications, see <see cref="Toasts"/>), and with it Windows draws the shortcut's icon, the
/// green one, on the button. A window with an ID of its own gets its own icon there, so the
/// button follows the board theme like the title bar. Pinning it still opens WyreMonitor.
/// </summary>
internal static class TaskbarButton
{
    public const string WindowId = "WyreMonitor.Window";

    public static void Apply(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return;
            try
            {
                var exe = Environment.ProcessPath ?? "";
                Set(store, 5, WindowId);                    // AppUserModel_ID
                Set(store, 2, $"\"{exe}\"");                // RelaunchCommand: a pinned button opens the app
                Set(store, 4, AppInfo.Name);                // RelaunchDisplayNameResource
                // No RelaunchIconResource: the taskbar would draw that icon (the exe's green one)
                // instead of the window's.
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            // Cosmetic: the button keeps the shortcut's icon.
            AppInfo.LogError(ex);
        }
    }

    private static void Set(IPropertyStore store, uint id, string value)
    {
        var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = id };
        var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref variant); }
        finally { PropVariantClear(ref variant); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PropVariantClear(ref PropVariant variant);
}
