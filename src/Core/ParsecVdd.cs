using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

/// <summary>
/// Direct client for the Parsec Virtual Display Driver (parsec-vdd protocol).
/// The driver removes every virtual display unless something pings it (VDD_IOCTL_UPDATE): after about a second
/// without a ping all of them are unplugged. This class keeps a keep-alive running for as long as the handle stays open:
/// a native TIME_CRITICAL thread with locked memory (tools\moonlight-vdd-keepalive.dll, which neither a GC pause nor
/// a page fault on a busy disk can stall), or a managed thread when the DLL is missing.
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
    private IntPtr _native;              // native keep-alive context, IntPtr.Zero while the managed loop (or nothing) runs
    private SafeFileHandle? _nativeHandleRef;
    private int _pingIntervalMs = 50;
    private DateTime _lastPingUtc;
    private int _consecutivePingFailures;
    private int _maxGapMs;               // managed loop: longest interval between two completed pings since the last report
    private int _longGaps;
    private bool _nativeAnnounced;

    public int PingIntervalMs
    {
        get => _pingIntervalMs;
        set
        {
            if (value == _pingIntervalMs) return;
            _pingIntervalMs = value;
            if (_native != IntPtr.Zero) { StopPing(); StartPing(); } // the native loop takes the interval at start
        }
    }

    public int Version { get; private set; } = -1;
    public string? DevicePath { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True while the keep-alive runs on the native thread of moonlight-vdd-keepalive.dll.</summary>
    public bool IsNativeKeepAlive => _native != IntPtr.Zero;

    public DateTime LastPingUtc
    {
        get
        {
            if (_native == IntPtr.Zero) return _lastPingUtc;
            var s = NativeKeepAlive.Stats(_native, false);
            return s.MsSinceLastPing < 0 ? default : DateTime.UtcNow - TimeSpan.FromMilliseconds(s.MsSinceLastPing);
        }
    }

    public int ConsecutivePingFailures => _native != IntPtr.Zero ? NativeKeepAlive.Stats(_native, false).ConsecutiveFailures : _consecutivePingFailures;

    /// <summary>Indices returned by the driver for displays added through this object (best effort bookkeeping).</summary>
    public List<int> AddedIndices { get; } = new();

    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };
    public bool IsPinging => _native != IntPtr.Zero ? NativeKeepAlive.Stats(_native, false).Running != 0 : _pinging && _pingThread is { IsAlive: true };

    /// <summary>Keep-alive health since the previous call (the watchdog reads it every tick).</summary>
    public readonly record struct KeepAliveReport(bool Native, bool MemoryLocked, long Pings, int MaxGapMs, int MaxIoMs, int LongGaps, int LastError);

    public KeepAliveReport TakeKeepAliveReport()
    {
        if (_native != IntPtr.Zero)
        {
            var s = NativeKeepAlive.Stats(_native, true);
            if (!_nativeAnnounced && s.Pings > 0)
            {
                _nativeAnnounced = true;
                Log.Info($"VDD keep-alive 运行在原生线程（TIME_CRITICAL，不受 GC 暂停影响，内存锁定: {(s.MemoryLocked != 0 ? "是" : "否")}）");
            }
            if (s.LastError != 0) LastError = "keep-alive 失败: Win32 错误 " + s.LastError;
            return new KeepAliveReport(true, s.MemoryLocked != 0, s.Pings, s.MaxGapMs, s.MaxIoMs, s.LongGaps, s.LastError);
        }
        return new KeepAliveReport(false, false, 0, Interlocked.Exchange(ref _maxGapMs, 0), 0, Interlocked.Exchange(ref _longGaps, 0), 0);
    }

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

    /// <summary>True when the adapter exposes its device interface, i.e. the driver is installed and the device started.</summary>
    public static bool IsDriverPresent() => FindDevicePath() != null;

    public enum DeviceHealth
    {
        /// <summary>No "Root\Parsec\VDA" device node: the driver package was never installed (or was uninstalled).</summary>
        Missing,
        /// <summary>Device node exists and is started.</summary>
        Ok,
        /// <summary>Device node exists but reports a problem code (typically 43 FAILED_POST_START after the user-mode driver host died).</summary>
        Problem,
        /// <summary>Device node exists but was disabled in Device Manager.</summary>
        Disabled,
    }

    public sealed record DeviceState(DeviceHealth Health, string InstanceId, uint Status, uint Problem)
    {
        public string Text => Health switch
        {
            DeviceHealth.Missing => "驱动未安装",
            DeviceHealth.Ok => "设备正常",
            DeviceHealth.Disabled => "设备已被禁用",
            _ => Problem switch
            {
                NativeMethods.CM_PROB_FAILED_POST_START => "设备异常 (代码 43，驱动进程曾停止响应)",
                NativeMethods.CM_PROB_FAILED_START => "设备无法启动 (代码 10)",
                _ => $"设备异常 (代码 {Problem})"
            }
        };
    }

    /// <summary>
    /// PnP state of the adapter device node, found by hardware id (the instance id is usually ROOT\DISPLAY\0000 but other
    /// virtual display drivers may have taken that slot). Pure SetupAPI/cfgmgr32: no pnputil process, safe to call every tick.
    /// </summary>
    public static DeviceState QueryDeviceState()
    {
        var guid = Guid.Empty;
        var set = NativeMethods.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, NativeMethods.DIGCF_ALLCLASSES | NativeMethods.DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new DeviceState(DeviceHealth.Missing, string.Empty, 0, 0);
        try
        {
            var info = new NativeMethods.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>() };
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(set, ref info, NativeMethods.SPDRP_HARDWAREID, out _, buffer, 4096, out var size) || size < 4) continue;
                    var ids = Marshal.PtrToStringUni(buffer, (int)size / 2) ?? string.Empty;
                    if (!ids.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;

                    var instanceId = string.Empty;
                    if (NativeMethods.SetupDiGetDeviceInstanceIdW(set, ref info, buffer, 2048, out _)) instanceId = Marshal.PtrToStringUni(buffer) ?? string.Empty;
                    if (NativeMethods.CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0) != 0)
                    {
                        return new DeviceState(DeviceHealth.Problem, instanceId, 0, 0);
                    }
                    var health = (status & NativeMethods.DN_HAS_PROBLEM) != 0
                        ? (problem == NativeMethods.CM_PROB_DISABLED ? DeviceHealth.Disabled : DeviceHealth.Problem)
                        : (status & NativeMethods.DN_STARTED) != 0 ? DeviceHealth.Ok : DeviceHealth.Problem;
                    return new DeviceState(health, instanceId, status, problem);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return new DeviceState(DeviceHealth.Missing, string.Empty, 0, 0);
    }

    /// <summary>Human readable adapter status for the UI / logs.</summary>
    public static string QueryAdapterStatus()
    {
        try { return QueryDeviceState().Text; }
        catch (Exception ex) { return "unknown: " + ex.Message; }
    }

    /// <summary>
    /// Restarts the adapter device node (disable/enable through pnputil). Needs administrator rights: when this process
    /// is elevated it runs directly, otherwise a UAC prompt for an elevated helper copy of the hub appears.
    /// </summary>
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

    /// <summary>Restarts the adapter and waits until its device interface is back (caller must be elevated).</summary>
    public static (bool Ok, string Message) RestartAdapterAndWait(TimeSpan wait)
    {
        var (ok, msg) = RestartAdapter();
        if (!ok) return (false, msg);
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (IsDriverPresent()) return (true, msg);
            Thread.Sleep(500);
        }
        return (false, "适配器已重启，但设备接口没有在 " + wait.TotalSeconds + " 秒内出现: " + QueryAdapterStatus());
    }

    /// <summary>The driver installer shipped next to the hub (Parsec's signed parsec-vdd-setup.exe, run with -silent like ParsecVDisplay does).</summary>
    public static string BundledSetupPath => System.IO.Path.Combine(Paths.ExeDir, "drivers", "parsec-vdd-setup.exe");
    public static bool HasBundledSetup => System.IO.File.Exists(BundledSetupPath);

    /// <summary>
    /// Installs (or repairs) the Parsec virtual display driver from the bundled installer. Needs administrator rights;
    /// the installer itself elevates when this process is not elevated (UAC prompt).
    /// </summary>
    public static (bool Ok, string Message) InstallDriver()
    {
        if (!HasBundledSetup) return (false, "找不到随附的驱动安装包 " + BundledSetupPath);
        try
        {
            Log.Info("开始安装 Parsec 虚拟显示驱动: " + BundledSetupPath + " -silent");
            var psi = new System.Diagnostics.ProcessStartInfo(BundledSetupPath, "-silent")
            {
                UseShellExecute = true,
                Verb = ProcessUtil.IsElevated() ? string.Empty : "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = System.IO.Path.GetDirectoryName(BundledSetupPath) ?? Paths.ExeDir,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (false, "无法启动驱动安装程序");
            if (!p.WaitForExit(240000)) { try { p.Kill(); } catch { } return (false, "驱动安装程序超时"); }
            Log.Info($"驱动安装程序退出码 {p.ExitCode}");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (IsDriverPresent()) return (true, "驱动已安装，设备已就绪");
                Thread.Sleep(1000);
            }
            var state = QueryDeviceState();
            return state.Health switch
            {
                DeviceHealth.Missing => (false, $"安装程序已结束（退出码 {p.ExitCode}），但没有出现 Parsec 设备；可能需要重启电脑完成安装"),
                DeviceHealth.Ok => (true, "驱动已安装"),
                _ => (false, "驱动已安装但设备状态为: " + state.Text)
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "用户取消了 UAC 提示");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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
        _faulted = false;
        _consecutivePingFailures = 0;
        StateChanged?.Invoke();
    }

    public void StartPing()
    {
        if (IsPinging || !IsOpen) return;
        StopPing(); // release a native context that stopped by itself (driver fault) before starting a new one
        if (StartNativePing()) return;
        _pinging = true;
        _pingThread = new Thread(PingLoop) { IsBackground = true, Name = "ParsecVddKeepAlive", Priority = ThreadPriority.Highest };
        _pingThread.Start();
    }

    private bool StartNativePing()
    {
        if (!OperatingSystem.IsWindows() || !NativeKeepAlive.Available) return false;
        var handle = _handle!;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added); // the native thread uses the raw handle: keep it from being closed underneath
            var context = NativeKeepAlive.Start(handle.DangerousGetHandle(), PingIntervalMs);
            if (context == IntPtr.Zero)
            {
                Log.Warn("原生 VDD keep-alive 启动失败（Win32 错误 " + Marshal.GetLastPInvokeError() + "），改用托管线程");
                if (added) handle.DangerousRelease();
                return false;
            }
            _native = context;
            _nativeHandleRef = handle;
            return true;
        }
        catch (Exception ex)
        {
            if (added) handle.DangerousRelease();
            Log.Warn("原生 VDD keep-alive 不可用（" + ex.Message + "），改用托管线程");
            return false;
        }
    }

    public void StopPing()
    {
        var native = _native;
        if (native != IntPtr.Zero)
        {
            // take the final counters first: a fault seen here must survive the release of the context
            var s = NativeKeepAlive.Stats(native, false);
            if (s.Faulted != 0) { _faulted = true; LastFaultUtc = DateTime.UtcNow; }
            _native = IntPtr.Zero;
            if (NativeKeepAlive.Stop(native))
            {
                _nativeHandleRef?.DangerousRelease();
            }
            else
            {
                Log.Warn("原生 VDD keep-alive 线程仍卡在驱动调用里，放弃等待（句柄保持引用，不会被关闭）");
            }
            _nativeHandleRef = null;
        }

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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastOk = -1;
        while (_pinging)
        {
            try
            {
                Update();
                var now = clock.ElapsedMilliseconds;
                if (lastOk >= 0)
                {
                    var gap = (int)(now - lastOk);
                    if (gap > Volatile.Read(ref _maxGapMs)) Interlocked.Exchange(ref _maxGapMs, gap);
                    if (gap >= 300) Interlocked.Increment(ref _longGaps);
                }
                lastOk = now;
                _lastPingUtc = DateTime.UtcNow;
                _consecutivePingFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutivePingFailures++;
                LastError = "keep-alive 失败: " + ex.Message;
                if (_consecutivePingFailures == 1) Log.Warn("VDD keep-alive 失败: " + ex.Message);
                if (Faulted) { _pinging = false; break; } // the device is gone; the watchdog restarts it
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
    /// <summary>Set when an IOCTL timed out: the user-mode driver host is most likely gone and the device needs a restart.</summary>
    public bool Faulted => _faulted || _native != IntPtr.Zero && NativeKeepAlive.Stats(_native, false).Faulted != 0;
    private bool _faulted;
    public DateTime LastFaultUtc { get; private set; }

    private int IoControl(uint code, ReadOnlySpan<byte> input)
    {
        var handle = _handle ?? throw new InvalidOperationException("Parsec VDD 设备未打开");
        // The OVERLAPPED block and both buffers live on the native heap: if the driver never completes the request
        // (dead UMDF host) the kernel may still touch them after we give up, so they are leaked instead of freed
        // (stack memory would have been reused → silent memory corruption).
        const int block = 64 + 32 + 32;
        var mem = (byte*)NativeMemory.AllocZeroed(block);
        var overlapped = (NativeOverlapped*)mem;
        var inBuffer = mem + 64;
        var outBuffer = mem + 96;
        var copy = Math.Min(input.Length, 32);
        for (var i = 0; i < copy; i++) inBuffer[i] = input[i];

        var started = System.Diagnostics.Stopwatch.StartNew();
        overlapped->EventHandle = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
        var completed = false;
        try
        {
            var ok = NativeMethods.DeviceIoControl(handle, code, inBuffer, 32, outBuffer, 32, out _, overlapped);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != NativeMethods.ERROR_IO_PENDING)
                {
                    completed = true;
                    throw new InvalidOperationException($"DeviceIoControl(0x{code:X8}) 失败: Win32 错误 {err}");
                }
            }
            if (!NativeMethods.GetOverlappedResultEx(handle, overlapped, out _, IoTimeoutMs, false))
            {
                var err = Marshal.GetLastWin32Error();
                // give the driver a chance to complete the cancelled request so the buffers can be released
                NativeMethods.CancelIoEx(handle, overlapped);
                if (NativeMethods.GetOverlappedResultEx(handle, overlapped, out _, 3000, false))
                {
                    completed = true;
                }
                else
                {
                    var err2 = Marshal.GetLastWin32Error();
                    // 995 OPERATION_ABORTED = cancelled and done; 258 / 996 / 997 = still owned by the driver
                    completed = err2 != 258 && err2 != 996 && err2 != NativeMethods.ERROR_IO_PENDING;
                }
                if (code != IoctlUpdate || err != 258)
                {
                    _faulted = true;
                    LastFaultUtc = DateTime.UtcNow;
                }
                throw new InvalidOperationException($"DeviceIoControl(0x{code:X8}) 等待结果失败: Win32 错误 {err}");
            }
            completed = true;
            var result = *(int*)outBuffer;
            if (code != IoctlUpdate)
            {
                Log.Debug($"VDD IOCTL 0x{code:X8} => {result} ({started.ElapsedMilliseconds}ms)");
            }
            return result;
        }
        finally
        {
            if (overlapped->EventHandle != IntPtr.Zero) NativeMethods.CloseHandle(overlapped->EventHandle);
            if (completed) NativeMemory.Free(mem);
            else Log.Warn($"VDD IOCTL 0x{code:X8} 未完成，缓冲区保留不释放（驱动可能已停止响应）");
        }
    }

    public void Dispose()
    {
        Close();
    }

    /// <summary>Bindings for tools\moonlight-vdd-keepalive.dll (source: MoonlightHub\native\vdd-keepalive).</summary>
    private static class NativeKeepAlive
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct VkaStats
        {
            public int Running;
            public int Faulted;
            public int ConsecutiveFailures;
            public int LastError;
            public long Pings;
            public long Failures;
            public long MsSinceLastPing;
            public int MaxGapMs;
            public int MaxIoMs;
            public int LongGaps;
            public int MemoryLocked;
        }

        private static readonly delegate* unmanaged<IntPtr, int, IntPtr> StartFn;
        private static readonly delegate* unmanaged<IntPtr, int> StopFn;
        private static readonly delegate* unmanaged<IntPtr, VkaStats*, int, void> StatsFn;

        public static bool Available { get; }

        static NativeKeepAlive()
        {
            var path = System.IO.Path.Combine(Paths.ExeDir, "tools", "moonlight-vdd-keepalive.dll");
            if (!System.IO.File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) return;
            if (!NativeLibrary.TryGetExport(lib, "vka_start", out var start)
                || !NativeLibrary.TryGetExport(lib, "vka_stop", out var stop)
                || !NativeLibrary.TryGetExport(lib, "vka_stats", out var stats)) return;
            StartFn = (delegate* unmanaged<IntPtr, int, IntPtr>)start;
            StopFn = (delegate* unmanaged<IntPtr, int>)stop;
            StatsFn = (delegate* unmanaged<IntPtr, VkaStats*, int, void>)stats;
            Available = true;
        }

        public static IntPtr Start(IntPtr device, int intervalMs)
        {
            Marshal.SetLastPInvokeError(0);
            var context = StartFn(device, intervalMs);
            if (context == IntPtr.Zero) Marshal.SetLastPInvokeError(Marshal.GetLastSystemError());
            return context;
        }

        public static bool Stop(IntPtr context) => StopFn(context) != 0;

        public static VkaStats Stats(IntPtr context, bool resetPeaks)
        {
            VkaStats s;
            StatsFn(context, &s, resetPeaks ? 1 : 0);
            return s;
        }
    }
}
