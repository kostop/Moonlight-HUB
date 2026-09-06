using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

/// <summary>
/// Direct client for the Parsec Virtual Display Driver (parsec-vdd protocol).
/// The driver removes every virtual display unless something pings it (VDD_IOCTL_UPDATE) at least every ~100 ms,
/// so this class owns a dedicated high-priority keep-alive thread for as long as the handle stays open.
/// This replaces the old PowerShell controller, which only pinged every 30 s and therefore lost the display within seconds.
/// </summary>
public sealed unsafe class ParsecVdd : IDisposable
{
    public static readonly Guid AdapterInterfaceGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    public const string HardwareId = @"Root\Parsec\VDA";
    public const string AdapterName = "Parsec Virtual Display Adapter";
    public const string MonitorId = "PSCCDD0";
    public const string MonitorName = "ParsecVDA";
    public const int MaxDisplays = 8;

    private const uint IoctlAdd = 0x0022e004;
    private const uint IoctlRemove = 0x0022a008;
    private const uint IoctlUpdate = 0x0022a00c;
    private const uint IoctlVersion = 0x0022e010;
    private const int IoTimeoutMs = 5000;

    private SafeFileHandle? _handle;
    private Thread? _pingThread;
    private volatile bool _pinging;

    public int PingIntervalMs { get; set; } = 50;
    public int Version { get; private set; } = -1;
    public string? DevicePath { get; private set; }
    public DateTime LastPingUtc { get; private set; }
    public int ConsecutivePingFailures { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Indices returned by the driver for displays added through this object (best effort bookkeeping).</summary>
    public List<int> AddedIndices { get; } = new();

    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };
    public bool IsPinging => _pinging && _pingThread is { IsAlive: true };

    public event Action? StateChanged;

    public static string? FindDevicePath()
    {
        var guid = AdapterInterfaceGuid;
        var set = NativeMethods.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            var data = new NativeMethods.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                NativeMethods.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, 8); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (NativeMethods.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        var path = NativeMethods.ReadInterfaceDetailPath(buffer);
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return null;
    }

    public static bool IsDriverPresent() => FindDevicePath() != null;

    /// <summary>PnP status of the adapter device ("OK", "Error (code)", or "missing") via pnputil, for diagnostics.</summary>
    public static string QueryAdapterStatus()
    {
        try
        {
            var (code, output) = ProcessUtil.Run("pnputil.exe", "/enum-devices /instanceid \"ROOT\\DISPLAY\\0000\"", 15000);
            if (code != 0) return "missing";
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("状态:", StringComparison.Ordinal))
                {
                    return line[(line.IndexOf(':') + 1)..].Trim();
                }
            }
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Restarts the adapter device (needs administrator rights; re-launches the hub elevated when necessary).</summary>
    public static (bool Ok, string Message) RestartAdapter()
    {
        if (ProcessUtil.IsElevated())
        {
            var code = ElevatedOps.Run(new[] { "restart-parsec-adapter" });
            return code == 0 ? (true, "适配器已重启") : (false, $"pnputil 退出码 {code}");
        }
        var result = ProcessUtil.RunSelfElevated("--elevated restart-parsec-adapter", 90000);
        return result switch
        {
            0 => (true, "适配器已重启"),
            -1 => (false, "用户取消了 UAC 提示"),
            -2 => (false, "提权进程超时"),
            _ => (false, $"提权重启失败，退出码 {result}")
        };
    }

    public bool Open()
    {
        if (IsOpen) return true;
        var path = FindDevicePath();
        if (path == null)
        {
            LastError = "未找到 Parsec Virtual Display Adapter 设备接口（驱动未安装或已禁用）";
            return false;
        }

        var handle = NativeMethods.CreateFileW(path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_OVERLAPPED | NativeMethods.FILE_FLAG_WRITE_THROUGH,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            LastError = "打开 Parsec VDD 设备失败: Win32 错误 " + Marshal.GetLastWin32Error();
            handle.Dispose();
            return false;
        }

        _handle = handle;
        DevicePath = path;
        try
        {
            Version = IoControl(IoctlVersion, ReadOnlySpan<byte>.Empty);
        }
        catch (Exception ex)
        {
            LastError = "读取 VDD 版本失败: " + ex.Message;
            Version = -1;
        }

        StartPing();
        StateChanged?.Invoke();
        return true;
    }

    public void Close()
    {
        StopPing();
        _handle?.Dispose();
        _handle = null;
        StateChanged?.Invoke();
    }

    public void StartPing()
    {
        if (IsPinging || !IsOpen) return;
        _pinging = true;
        _pingThread = new Thread(PingLoop) { IsBackground = true, Name = "ParsecVddKeepAlive", Priority = ThreadPriority.Highest };
        _pingThread.Start();
    }

    public void StopPing()
    {
        _pinging = false;
        var t = _pingThread;
        _pingThread = null;
        if (t != null && t.IsAlive && Thread.CurrentThread != t)
        {
            t.Join(1000);
        }
    }

    private void PingLoop()
    {
        while (_pinging)
        {
            try
            {
                Update();
                LastPingUtc = DateTime.UtcNow;
                ConsecutivePingFailures = 0;
            }
            catch (Exception ex)
            {
                ConsecutivePingFailures++;
                LastError = "keep-alive 失败: " + ex.Message;
            }
            Thread.Sleep(Math.Clamp(PingIntervalMs, 10, 90));
        }
    }

    /// <summary>Adds a virtual display and returns the driver's display index (0..7).</summary>
    public int AddDisplay()
    {
        EnsureOpen();
        var index = IoControl(IoctlAdd, ReadOnlySpan<byte>.Empty);
        Update();
        if (index >= 0 && index < MaxDisplays && !AddedIndices.Contains(index)) AddedIndices.Add(index);
        StateChanged?.Invoke();
        return index;
    }

    public void RemoveDisplay(int index)
    {
        EnsureOpen();
        var be = (ushort)(((index & 0xFF) << 8) | ((index >> 8) & 0xFF));
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, be);
        IoControl(IoctlRemove, data);
        Update();
        AddedIndices.Remove(index);
        StateChanged?.Invoke();
    }

    public void RemoveAll()
    {
        for (var i = MaxDisplays - 1; i >= 0; i--)
        {
            try { RemoveDisplay(i); } catch { }
        }
        AddedIndices.Clear();
    }

    public void Update()
    {
        EnsureOpen();
        IoControl(IoctlUpdate, ReadOnlySpan<byte>.Empty);
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new InvalidOperationException("Parsec VDD 设备未打开");
    }

    /// <summary>
    /// One IOCTL round-trip. Deliberately lock-free: the driver completes ADD/REMOVE only while UPDATE pings keep
    /// arriving, so the keep-alive thread must never be blocked by another call (each call owns its buffers/event).
    /// </summary>
    private int IoControl(uint code, ReadOnlySpan<byte> input)
    {
        var handle = _handle ?? throw new InvalidOperationException("Parsec VDD 设备未打开");
        byte* inBuffer = stackalloc byte[32];
        byte* outBuffer = stackalloc byte[32];
        for (var i = 0; i < 32; i++) { inBuffer[i] = 0; outBuffer[i] = 0; }
        var copy = Math.Min(input.Length, 32);
        for (var i = 0; i < copy; i++) inBuffer[i] = input[i];

        var started = System.Diagnostics.Stopwatch.StartNew();
        var overlapped = new NativeOverlapped();
        overlapped.EventHandle = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
        try
        {
            var ok = NativeMethods.DeviceIoControl(handle, code, inBuffer, 32, outBuffer, 32, out _, &overlapped);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != NativeMethods.ERROR_IO_PENDING)
                {
                    throw new InvalidOperationException($"DeviceIoControl(0x{code:X8}) 失败: Win32 错误 {err}");
                }
            }
            if (!NativeMethods.GetOverlappedResultEx(handle, &overlapped, out _, IoTimeoutMs, false))
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DeviceIoControl(0x{code:X8}) 等待结果失败: Win32 错误 {err}");
            }
            var result = *(int*)outBuffer;
            if (code != IoctlUpdate)
            {
                Log.Debug($"VDD IOCTL 0x{code:X8} => {result} ({started.ElapsedMilliseconds}ms)");
            }
            return result;
        }
        finally
        {
            if (overlapped.EventHandle != IntPtr.Zero) NativeMethods.CloseHandle(overlapped.EventHandle);
        }
    }

    public void Dispose()
    {
        Close();
    }
}
