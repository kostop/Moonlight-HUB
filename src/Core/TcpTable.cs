using System.Net;
using System.Runtime.InteropServices;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

public sealed record TcpConnection(int LocalPort, string RemoteAddress, int RemotePort, uint State, int Pid)
{
    public bool Established => State == TcpTable.MIB_TCP_STATE_ESTAB;
}

/// <summary>Reads the TCP table (listeners and connections) without WMI.</summary>
public static unsafe class TcpTable
{
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const uint MIB_TCP_STATE_ESTAB = 5;

    public static int? GetListeningPid(int port)
    {
        return ScanV6(port) ?? ScanV4(port);
    }

    public static HashSet<int> GetListeningPorts(int pid)
    {
        var ports = new HashSet<int>();
        try
        {
            foreach (var (p, owner) in EnumerateListeners())
            {
                if (owner == pid) ports.Add(p);
            }
        }
        catch { }
        return ports;
    }

    public static IEnumerable<(int Port, int Pid)> EnumerateListeners()
    {
        foreach (var e in EnumerateTable(NativeMethods.AF_INET)) yield return e;
        foreach (var e in EnumerateTable(NativeMethods.AF_INET6)) yield return e;
    }

    /// <summary>All TCP connections (v4 + v6) with remote endpoints; IPv4-mapped IPv6 addresses are normalised to IPv4.</summary>
    public static List<TcpConnection> EnumerateConnections()
    {
        var list = new List<TcpConnection>();
        try { list.AddRange(EnumerateConnectionTable(NativeMethods.AF_INET)); } catch { }
        try { list.AddRange(EnumerateConnectionTable(NativeMethods.AF_INET6)); } catch { }
        return list;
    }

    /// <summary>Remote addresses currently connected to a local port (established only).</summary>
    public static List<string> RemoteAddressesFor(int localPort)
    {
        return EnumerateConnections()
            .Where(c => c.Established && c.LocalPort == localPort && !IsLocal(c.RemoteAddress))
            .Select(c => c.RemoteAddress)
            .Distinct()
            .ToList();
    }

    public static bool IsLocal(string address)
    {
        return address is "127.0.0.1" or "::1" || address.StartsWith("127.", StringComparison.Ordinal);
    }

    private static int? ScanV4(int port)
    {
        foreach (var (p, pid) in EnumerateTable(NativeMethods.AF_INET))
        {
            if (p == port) return pid;
        }
        return null;
    }

    private static int? ScanV6(int port)
    {
        foreach (var (p, pid) in EnumerateTable(NativeMethods.AF_INET6))
        {
            if (p == port) return pid;
        }
        return null;
    }

    private static IEnumerable<(int Port, int Pid)> EnumerateTable(int family)
    {
        var size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) yield break;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (result != 0) yield break;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            if (family == NativeMethods.AF_INET)
            {
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                    yield return (PortFromNbo(row.localPort), (int)row.owningPid);
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr + i * rowSize);
                    yield return (PortFromNbo(row.localPort), (int)row.owningPid);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<TcpConnection> EnumerateConnectionTable(int family)
    {
        var size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) yield break;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != 0) yield break;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            if (family == NativeMethods.AF_INET)
            {
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                    var remote = new IPAddress(row.remoteAddr).ToString();
                    yield return new TcpConnection(PortFromNbo(row.localPort), remote, PortFromNbo(row.remotePort), row.state, (int)row.owningPid);
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr + i * rowSize);
                    var ip = new IPAddress(RemoteAddressBytes(row));
                    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                    yield return new TcpConnection(PortFromNbo(row.localPort), ip.ToString(), PortFromNbo(row.remotePort), row.state, (int)row.owningPid);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] RemoteAddressBytes(NativeMethods.MIB_TCP6ROW_OWNER_PID row)
    {
        var bytes = new byte[16];
        for (var b = 0; b < 16; b++) bytes[b] = row.remoteAddr[b];
        return bytes;
    }

    private static int PortFromNbo(uint value)
    {
        var low = value & 0xFFFF;
        return (int)(((low & 0xFF) << 8) | ((low >> 8) & 0xFF));
    }
}
