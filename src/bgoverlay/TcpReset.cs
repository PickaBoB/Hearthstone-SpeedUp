using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BgOverlay;

internal static class TcpReset
{
    private const int Inet = 2;
    private const int OwnerPidAll = 5;
    private const uint InsufficientBuffer = 122;
    private const uint NotFound = 1168;
    private const uint Established = 5;
    private const uint DeleteTcb = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref TcpRow row);

    public static void ResetProcessConnections(int processId, uint remoteAddress, uint remotePort)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, Inet, OwnerPidAll, 0);
        if (result != 0 && result != InsufficientBuffer)
            throw new Win32Exception((int)result, "Не удалось прочитать TCP-соединения Hearthstone.");

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(table, ref size, false, Inet, OwnerPidAll, 0);
            if (result != 0)
                throw new Win32Exception((int)result, "Не удалось прочитать TCP-соединения Hearthstone.");

            int reset = 0;
            int count = Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<TcpRow>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(table + sizeof(int) + i * rowSize);
                if (row.ProcessId != processId || row.State != Established
                    || row.RemoteAddress != remoteAddress || row.RemotePort != remotePort)
                    continue;
                row.State = DeleteTcb;
                result = SetTcpEntry(ref row);
                if (result != 0 && result != NotFound)
                    throw new Win32Exception((int)result, "Не удалось сбросить соединение Hearthstone.");
                reset++;
            }
            if (reset == 0)
                throw new InvalidOperationException("Активное соединение с игровым сервером не найдено.");
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }
}
