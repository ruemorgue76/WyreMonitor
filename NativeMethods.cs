using System.Runtime.InteropServices;

namespace WyreMonitor;

internal static class NativeMethods
{
    // Search only the app's folder and System32 for DLLs loaded by name. Without this, a DLL the
    // sensor library asks for (a vendor driver DLL that isn't installed, say) would also be looked
    // for in the folders of the system PATH, some of which normal users can write to: a planted
    // DLL would then run inside the service as SYSTEM.
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    public static void RestrictDllSearch() => SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    /// <summary>The process listening on 127.0.0.1:port over TCP, or null.</summary>
    public static int? ListeningProcess(int port)
    {
        const int AfInet = 2;
        const int TcpTableOwnerPidListener = 3;
        const uint ErrorInsufficientBuffer = 122;
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        var buffer = IntPtr.Zero;
        try
        {
            // The table can grow between the size query and the read: retry with the new size.
            uint result = ErrorInsufficientBuffer;
            for (var attempt = 0; attempt < 5 && result == ErrorInsufficientBuffer; attempt++)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
                result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            }
            if (result != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + i * rowSize);
                // The port is in network byte order in the low 16 bits.
                var rowPort = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                if (rowPort == port && row.LocalAddr == 0x0100007F) return (int)row.OwningPid;   // 127.0.0.1
            }
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);
}
