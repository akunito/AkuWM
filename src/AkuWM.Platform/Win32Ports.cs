using System.Runtime.InteropServices;

namespace AkuWM.Platform;

/// <summary>
/// Who owns a listening TCP port, for <c>doctor</c>.
/// </summary>
/// <remarks>
/// A process can exit and still hold its socket: a GlazeWM watcher stuck in
/// kernel teardown kept 6123 bound for a day with a pid that no longer
/// existed, and "something is listening" was all the doctor could say.
/// </remarks>
public static class Win32Ports
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int NoError = 0;
    private const int InsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    /// <summary>The pid listening on that port, or null when nobody is.</summary>
    public static int? ListenerOf(int port)
    {
        int size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != InsufficientBuffer)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != NoError)
            {
                return null;
            }

            // MIB_TCPTABLE_OWNER_PID: dwNumEntries, then rows of six DWORDs --
            // state, local address, local port (network order in the low
            // word), remote address, remote port, owning pid.
            int entries = Marshal.ReadInt32(buffer);
            for (int i = 0; i < entries; i++)
            {
                IntPtr row = buffer + 4 + (i * 24);
                int localPort = Marshal.ReadInt32(row + 8);
                int hostPort = ((localPort & 0xFF) << 8) | ((localPort >> 8) & 0xFF);
                if (hostPort == port)
                {
                    return Marshal.ReadInt32(row + 20);
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
