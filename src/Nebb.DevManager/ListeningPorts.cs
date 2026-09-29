using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Nebb.DevManager;

internal static class ListeningPorts
{
    private const int TcpTableOwnerPidListener = 3;
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
        int addressFamily, int tableClass, uint reserved);

    public static IReadOnlyList<int> Owners(int port)
    {
        return All().Where(pair => pair.Port == port).Select(pair => pair.ProcessId)
            .Distinct().ToArray();
    }

    public static IReadOnlyList<(int Port, int ProcessId)> All()
    {
        var listeners = new HashSet<(int Port, int ProcessId)>();
        Read(AddressFamilyInterNetwork, 24, 8, 20, listeners);
        Read(AddressFamilyInterNetworkV6, 56, 20, 52, listeners);
        return listeners.ToArray();
    }

    private static void Read(int family, int rowSize, int portOffset, int pidOffset,
        HashSet<(int Port, int ProcessId)> listeners)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family,
            TcpTableOwnerPidListener, 0);
        if (size <= 4) return;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(table, ref size, false, family,
                TcpTableOwnerPidListener, 0);
            if (result != 0) throw new Win32Exception((int)result, "TCP 포트 상태를 읽을 수 없습니다.");
            var count = Marshal.ReadInt32(table);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, 4 + index * rowSize);
                var raw = Marshal.ReadInt32(row, portOffset) & 0xFFFF;
                var localPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                listeners.Add((localPort, Marshal.ReadInt32(row, pidOffset)));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
    }
}
