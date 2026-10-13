using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MoonlightHub.Core;

/// <summary>
/// Per-screen mouse speed (Windows). Windows has one pointer speed, but the screens the cursor visits differ a lot in
/// pixel density: the tablet's 10" 2000x1200 virtual display (175 %), the laptop panel, the Dell, and the other
/// computer's 27" monitor reached through Deskflow — which moves that cursor by this computer's pixel deltas, so the
/// speed set here is the speed over there too. The hub switches the pointer speed whenever the cursor enters another
/// screen: automatically from the screen's display scale relative to the primary physical display (a 175 % screen gets
/// 1.75x the movement of a 100 % one), or the speed the user picked for that screen. The user's own setting
/// (Control Panel\Mouse\MouseSensitivity) is the baseline; changes are never written to disk, so a hub that stops leaves
/// the next logon untouched.
/// </summary>
public sealed class PointerSpeedManager : IDisposable
{
    /// <summary>Windows pointer speed 1..20 → movement multiplier (index = speed).</summary>
    private static readonly double[] Multipliers =
        { 0, 1 / 32.0, 1 / 16.0, 1 / 8.0, 1 / 4.0, 3 / 8.0, 1 / 2.0, 5 / 8.0, 3 / 4.0, 7 / 8.0, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5 };

    public sealed record ScreenSpeed(string Key, string Label, double Scale, int AutoSpeed, int? ManualSpeed, bool IsPeer)
    {
        public int Speed => ManualSpeed ?? AutoSpeed;
    }

    private readonly HubSettings _settings;
    private readonly DeskflowManager _deskflow;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private List<(IntPtr Monitor, ScreenSpeed Screen)> _monitors = new();
    private List<ScreenSpeed> _peers = new();
    private DateTime _lastRebuildUtc = DateTime.MinValue;
    private List<DisplayInfo> _displays = new();
    private DateTime _lastEnumerateUtc = DateTime.MinValue;
    private long _logPos = -1;
    private string _logPath = string.Empty;
    private string? _remoteScreen;
    private int? _corePid;
    private int _applied;
    private double _referenceScale = 1.0;

    public PointerSpeedManager(HubSettings settings, DeskflowManager deskflow)
    {
        _settings = settings;
        _deskflow = deskflow;
    }

    /// <summary>The user's own pointer speed (1..20) from the registry.</summary>
    public int Baseline { get; private set; } = 10;
    /// <summary>Key of the screen the cursor is on (a local monitor or a Deskflow peer).</summary>
    public string CurrentKey { get; private set; } = string.Empty;
    public int CurrentSpeed { get; private set; }

    /// <summary>All screens with their automatic and chosen speed (for the UI).</summary>
    public IReadOnlyList<ScreenSpeed> Screens
    {
        get
        {
            lock (_gate) return _monitors.Select(m => m.Screen).Concat(_peers).ToList();
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PointerSpeed" };
        _thread.Start();
    }

    /// <summary>Stores a speed for one screen (null = automatic) and applies it right away when the cursor is there.</summary>
    public void SetManual(string key, int? speed)
    {
        if (speed is { } s) _settings.PointerSpeeds[key] = Math.Clamp(s, 1, 20);
        else _settings.PointerSpeeds.Remove(key);
        _settings.Save();
        _lastRebuildUtc = DateTime.MinValue;
    }

    public void Invalidate() => _lastRebuildUtc = DateTime.MinValue;

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Debug("鼠标速度: " + ex.Message);
                Thread.Sleep(1000);
            }
            Thread.Sleep(50);
        }
    }

    private void Tick()
    {
        if (!_settings.PointerSpeedEnabled)
        {
            RestoreBaseline();
            CurrentKey = string.Empty;
            Thread.Sleep(450);
            return;
        }
        if ((DateTime.UtcNow - _lastRebuildUtc) > TimeSpan.FromSeconds(2)) Rebuild();
        FollowDeskflow();

        string key;
        ScreenSpeed? screen;
        lock (_gate)
        {
            if (_remoteScreen != null)
            {
                var index = _peers.FindIndex(p => p.Label.Equals(_remoteScreen, StringComparison.OrdinalIgnoreCase));
                screen = index >= 0 ? _peers[index] : null;
                var peer = _deskflow.PeerHub;
                if (screen != null && peer != null && peer.Screen.Equals(_remoteScreen, StringComparison.OrdinalIgnoreCase)
                    && peer.DpiScale > 0 && Math.Abs(screen.Scale - peer.DpiScale) > 0.001)
                {
                    // 对端光标换屏后立即跟随真实 DPI；保留用户已经选择的手动速度。
                    screen = screen with { Scale = peer.DpiScale,
                        AutoSpeed = Closest(Multipliers[Math.Clamp(Baseline, 1, 20)] * peer.DpiScale / _referenceScale) };
                    _peers[index] = screen;
                }
                key = screen?.Key ?? "peer:" + _remoteScreen;
            }
            else
            {
                if (!GetCursorPos(out var pt)) return;
                var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                var found = _monitors.FirstOrDefault(m => m.Monitor == monitor);
                if (found.Screen == null)
                {
                    _lastRebuildUtc = DateTime.MinValue; // a monitor we have not seen: topology changed
                    return;
                }
                screen = found.Screen;
                key = screen.Key;
            }
        }
        var speed = screen?.Speed ?? Baseline;
        CurrentKey = key;
        CurrentSpeed = speed;
        if (GetSpeed() != speed && SetSpeed(speed)) _applied = speed;
    }

    /// <summary>Re-reads the baseline, the monitors with their scale and the Deskflow peers.</summary>
    private void Rebuild()
    {
        _lastRebuildUtc = DateTime.UtcNow;
        Baseline = ReadBaseline();
        if ((DateTime.UtcNow - _lastEnumerateUtc) > TimeSpan.FromSeconds(10) || _displays.Count == 0)
        {
            _lastEnumerateUtc = DateTime.UtcNow;
            try { _displays = DisplayManager.Enumerate(); } catch { }
        }

        var monitors = new List<(IntPtr Handle, string Device, bool Primary, double Scale)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                var scale = GetDpiForMonitor(h, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
                monitors.Add((h, info.szDevice, (info.dwFlags & 1) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);

        // reference: the primary physical display (the user tuned the baseline there); a 100 % screen when only virtual
        // displays are on
        var reference = 1.0;
        var physicalPrimary = monitors.FirstOrDefault(m => m.Primary && !IsParsec(m.Device));
        if (physicalPrimary.Device != null) reference = physicalPrimary.Scale;
        else if (monitors.Where(m => !IsParsec(m.Device)).Select(m => m.Scale).DefaultIfEmpty(0).Min() is var min && min > 0) reference = min;

        var list = new List<(IntPtr, ScreenSpeed)>();
        foreach (var m in monitors)
        {
            var d = _displays.FirstOrDefault(x => x.GdiName.Equals(m.Device, StringComparison.OrdinalIgnoreCase));
            var key = "monitor:" + (string.IsNullOrEmpty(d?.SunshineDeviceId) ? m.Device : d!.SunshineDeviceId);
            var label = d != null ? $"{d.Label} ({m.Device.Replace(@"\\.\", string.Empty)})" : m.Device;
            list.Add((m.Handle, Make(key, label, m.Scale / reference, m.Scale, false)));
        }

        var peers = new List<ScreenSpeed>();
        if (_deskflow.Status.RunningRole == DeskflowRole.Server && _deskflow.Layout?.Layout["screens"] is System.Text.Json.Nodes.JsonObject screens)
        {
            foreach (var kv in screens)
            {
                if (kv.Key.Equals(_deskflow.LocalScreenName, StringComparison.OrdinalIgnoreCase)) continue;
                var hub = _deskflow.PeerHub;
                var scale = hub != null && hub.Screen.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && hub.DpiScale > 0 ? hub.DpiScale : 1.0;
                peers.Add(Make("peer:" + kv.Key, kv.Key, scale / reference, scale, true));
            }
        }

        lock (_gate)
        {
            _referenceScale = reference;
            _monitors = list;
            _peers = peers;
        }
    }

    private ScreenSpeed Make(string key, string label, double factor, double scale, bool peer)
    {
        int? manual = _settings.PointerSpeeds.TryGetValue(key, out var s) ? Math.Clamp(s, 1, 20) : null;
        return new ScreenSpeed(key, label, scale, Closest(Multipliers[Math.Clamp(Baseline, 1, 20)] * factor), manual, peer);
    }

    private static int Closest(double multiplier)
    {
        var best = 10;
        var bestDistance = double.MaxValue;
        for (var s = 1; s <= 20; s++)
        {
            var distance = Math.Abs(Math.Log(Multipliers[s] / multiplier));
            if (distance < bestDistance) { bestDistance = distance; best = s; }
        }
        return best;
    }

    private bool IsParsec(string device) =>
        _displays.Any(d => d.IsParsec && d.GdiName.Equals(device, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tracks normal switches and the forced jump home when a Deskflow client disconnects.
    /// A disconnected peer must never keep overriding the local monitor's pointer speed.
    /// </summary>
    private void FollowDeskflow()
    {
        if (_deskflow.Status.RunningRole != DeskflowRole.Server)
        {
            _remoteScreen = null;
            _corePid = null;
            _logPos = -1;
            return;
        }
        if (_corePid != _deskflow.Status.CorePid)
        {
            _corePid = _deskflow.Status.CorePid;
            _remoteScreen = null;
        }
        var path = _deskflow.Host.CoreLogPath;
        if (path != _logPath) { _logPath = path; _logPos = -1; _remoteScreen = null; }
        var info = new FileInfo(path);
        if (!info.Exists) { _remoteScreen = null; _logPos = -1; return; }
        if (_logPos < 0 || info.Length < _logPos)
        {
            _remoteScreen = null;
            _logPos = info.Length;
            return; // start at the end; log was rotated
        }
        if (info.Length == _logPos) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(_logPos, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(65536, info.Length - _logPos)];
        var read = stream.Read(buffer, 0, buffer.Length);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return; // wait for the whole line
        _logPos += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);
        _remoteScreen = FollowDeskflowLog(_remoteScreen, _deskflow.LocalScreenName, text[..(lastNewline + 1)]);
    }

    /// <param name="remoteScreen">当前远端屏幕；null 表示鼠标在本机。</param>
    /// <param name="localScreen">本机的 Deskflow 屏幕名称。</param>
    /// <param name="text">已经完整读取的日志行，按事件发生顺序处理。</param>
    private static string? FollowDeskflowLog(string? remoteScreen, string localScreen, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var move = SwitchPattern.Match(line);
            if (move.Success)
            {
                var to = move.Groups[1].Value;
                remoteScreen = to.Equals(localScreen, StringComparison.OrdinalIgnoreCase) ? null : to;
                continue;
            }
            if (line.Contains("started server", StringComparison.OrdinalIgnoreCase)
                || line.Contains("stopped server", StringComparison.OrdinalIgnoreCase))
            {
                remoteScreen = null;
                continue;
            }
            var disconnected = DisconnectPattern.Match(line);
            if (disconnected.Success && disconnected.Groups["screen"].Value.Equals(remoteScreen, StringComparison.OrdinalIgnoreCase))
                remoteScreen = null;
        }
        return remoteScreen;
    }

    private static readonly Regex SwitchPattern = new(@"(?:switch|jump) from ""[^""]*"" to ""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DisconnectPattern = new(@"(?:client ""(?<screen>[^""]+)"" (?:has disconnected|is dead)|(?:disconnecting client|forced disconnection of client) ""(?<screen>[^""]+)"")", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int ReadBaseline()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            return int.TryParse(key?.GetValue("MouseSensitivity") as string, out var v) && v is >= 1 and <= 20 ? v : 10;
        }
        catch
        {
            return 10;
        }
    }

    private void RestoreBaseline()
    {
        if (_applied == 0) return;
        var baseline = ReadBaseline();
        if (GetSpeed() != baseline) SetSpeed(baseline);
        _applied = 0;
        CurrentSpeed = baseline;
    }

    private static int GetSpeed()
    {
        var speed = 0;
        return SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref speed, 0) ? speed : 0;
    }

    /// <summary>Sets the pointer speed for this session only (no SPIF_UPDATEINIFILE: the user's saved setting stays).</summary>
    private static bool SetSpeed(int speed) => SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(Math.Clamp(speed, 1, 20)), 0);

    /// <summary>直接读取当前激活显示器的 Windows 缩放，不使用鼠标速度缓存。</summary>
    /// <param name="gdiName">目标显示器的 GDI 名称，例如 \\.\DISPLAY1。</param>
    /// <returns>Windows 缩放百分比；显示器未激活、未找到或读取失败时返回 0。</returns>
    public static int GetWindowsScalePercent(string gdiName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(gdiName)) return 0;
        var percent = 0;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(monitor, ref info) || !info.szDevice.Equals(gdiName, StringComparison.OrdinalIgnoreCase)) return true;
                if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0)
                    percent = (int)Math.Round(dpi * 100.0 / 96);
                return false;
            }, IntPtr.Zero);
        }
        catch { return 0; }
        return percent;
    }

    /// <summary>读取光标所在活动显示器的真实 DPI；不依赖自动鼠标速度是否启用。</summary>
    /// <returns>DPI 相对于 96 的倍率；非 Windows、光标不可读取或没有活动显示器时返回 0。</returns>
    public static double CurrentCursorScale()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            if (!GetCursorPos(out var point)) return 0;
            var monitor = MonitorFromPoint(point, 0 /* MONITOR_DEFAULTTONULL */);
            return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 0;
        }
        catch { return 0; }
    }

    /// <summary>主显示器的 DPI 倍率，光标所在显示器无法读取时可作为回退。</summary>
    public static double PrimaryScale()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Size of the whole desktop (all active monitors) in physical pixels — what Deskflow uses as this screen.</summary>
    public static (int Width, int Height) DesktopSize()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        try { return (GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */), GetSystemMetrics(79 /* SM_CYVIRTUALSCREEN */)); }
        catch { return (0, 0); }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        RestoreBaseline();
    }

    // ------------------------------------------------------------------ Win32
    private const uint SPI_GETMOUSESPEED = 0x0070;
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int rcLeft, rcTop, rcRight, rcBottom;
        public int wkLeft, wkTop, wkRight, wkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
}
