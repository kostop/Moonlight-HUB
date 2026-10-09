using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>What this computer does in the Deskflow keyboard/mouse sharing setup.</summary>
public enum DeskflowRole
{
    /// <summary>No Deskflow core runs.</summary>
    Off,
    /// <summary>This computer's keyboard and mouse control the other screens.</summary>
    Server,
    /// <summary>This computer is controlled by the Deskflow server at <see cref="DeskflowSettings.RemoteHost"/>.</summary>
    Client
}

/// <summary>A side-channel program (clipboard / file transfer / layout sync script) the hub keeps running (Windows).</summary>
public sealed class SidecarSpec
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Scheduled task it came from (disabled while the hub runs it).</summary>
    public string TaskName { get; set; } = string.Empty;
    public string Exe { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SidecarSpec Clone() => (SidecarSpec)MemberwiseClone();
}

/// <summary>
/// Deskflow options owned by <see cref="DeskflowManager"/>. Kept in its own file (deskflow.json) so that a role switched
/// from the command line is picked up by the running hub instead of being overwritten by its in-memory settings.
/// </summary>
public sealed class DeskflowSettings
{
    /// <summary>The hub runs deskflow-core itself and keeps it in <see cref="Role"/> (Deskflow's own autostart is switched off).</summary>
    public bool Managed { get; set; } = true;
    public DeskflowRole Role { get; set; } = DeskflowRole.Server;
    /// <summary>The other computer (IP, typed in by the user): server address in client mode, peer hub, layout service, side channel.</summary>
    public string RemoteHost { get; set; } = string.Empty;
    /// <summary>Switch the other computer's hub together with this one (it becomes server when this one becomes client and vice versa).</summary>
    public bool LinkPeer { get; set; } = true;
    /// <summary>Windows: server mode runs through the touch filter wrapper so tablet touches do not move the Deskflow cursor.</summary>
    public bool UseTouchFilter { get; set; } = true;
    /// <summary>Windows: keep the "keyboard-release on touch" monitor running in server mode.</summary>
    public bool KeyboardReleaseMonitor { get; set; } = true;
    /// <summary>
    /// Windows: tablet touches never move the real mouse. In server mode the touch filter inside Deskflow does it; in every
    /// other state (client, off, core restarting) the hub runs the stand-alone isolator in bridge mode.
    /// </summary>
    public bool TouchIsolation { get; set; } = true;
    /// <summary>Windows: Deskflow install directory (deskflow-core.exe); empty = detect.</summary>
    public string InstallDir { get; set; } = string.Empty;
    /// <summary>Set once the role was seeded from Deskflow.conf (first run keeps whatever role was configured before).</summary>
    public bool Initialized { get; set; }
    /// <summary>Deskflow's own autostart / background pieces were switched off and are replaced by the hub.</summary>
    public bool LegacyTakenOver { get; set; }
    /// <summary>Windows: side-channel programs formerly started by scheduled tasks, now run by the hub.</summary>
    public List<SidecarSpec> Sidecars { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath => Path.Combine(Paths.DataDir, "deskflow.json");

    public static DeskflowSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<DeskflowSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new DeskflowSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("读取 deskflow.json 失败，使用默认值: " + ex.Message);
        }
        return new DeskflowSettings();
    }

    public void Save()
    {
        Paths.EnsureDataDirs();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, true);
    }

    public DeskflowSettings Clone()
    {
        var c = (DeskflowSettings)MemberwiseClone();
        c.Sidecars = Sidecars.Select(s => s.Clone()).ToList();
        return c;
    }
}

/// <summary>One observation of the Deskflow core, configuration and connections.</summary>
public sealed class DeskflowStatus
{
    public bool Installed { get; init; }
    /// <summary>Role written in Deskflow.conf ([core] coreMode: 1 = client, 2 = server).</summary>
    public DeskflowRole ConfiguredRole { get; init; }
    /// <summary>Role of the core that is actually running (Off when none).</summary>
    public DeskflowRole RunningRole { get; init; }
    public int? CorePid { get; init; }
    /// <summary>Number of independent cores (a touch filter wrapper and its child count as one).</summary>
    public int MainCores { get; init; }
    /// <summary>Windows: the running server core was started through the touch filter wrapper.</summary>
    public bool Filtered { get; init; }
    public bool? FilterHealthy { get; init; }
    public bool F24Rule { get; init; }
    public bool Listening { get; init; }
    /// <summary>Server mode: connected clients; client mode: the server this computer is connected to.</summary>
    public List<string> Peers { get; init; } = new();
    public bool MonitorRunning { get; init; }
    /// <summary>Windows: the stand-alone touch isolator (bridge mode) is running.</summary>
    public bool TouchBridgeRunning { get; init; }
    public string RemoteHost { get; init; } = string.Empty;
    public string ScreenName { get; init; } = string.Empty;
    public int Port { get; init; } = 24800;
    public bool Tls { get; init; }
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;

    public string Summary => !Installed ? "未安装"
        : RunningRole switch
        {
            DeskflowRole.Server => Peers.Count > 0 ? $"服务端 · 已连接 {string.Join(", ", Peers)}" : (Listening ? $"服务端 · 等待客户端 (:{Port})" : "服务端 · 启动中"),
            DeskflowRole.Client => Peers.Count > 0 ? $"客户端 · 已连接 {Peers[0]}" : $"客户端 · 正在连接 {RemoteHost}",
            _ => "已停止"
        };
}

/// <summary>
/// Runs Deskflow for the hub on both systems: starts deskflow-core in the chosen role (server / client / off), keeps it
/// healthy (5 s guard with back-off), switches the other computer's hub along (<see cref="PeerLink"/>), and exposes the
/// shared screen layout so the display views can draw the other computer next to the local displays.
/// </summary>
public sealed class DeskflowManager : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly System.Threading.Timer _timer;
    private readonly IDeskflowHost _host;
    private readonly PeerLink _link;
    private DeskflowSettings _settings;
    private DateTime _settingsStamp;
    private int _badTicks;
    private DateTime _lastRepairUtc = DateTime.MinValue;
    private TimeSpan _repairBackoff = TimeSpan.FromSeconds(15);
    private bool _started;
    private int _ticking;
    private bool _takeOverAttempted;
    private DateTime _lastLayoutFetchUtc = DateTime.MinValue;
    private int _layoutFetching;
    private DateTime _lastPeerStatusUtc = DateTime.MinValue;
    private int _peerFetching;
    private DateTime _lastReloadRequestUtc = DateTime.MinValue;
    /// <summary>First time each core TCP connection was seen: a peer only counts once its connection has lasted a few seconds
    /// (a client retrying a server that never completes the handshake opens a new short-lived connection every few seconds).</summary>
    private readonly Dictionary<string, DateTime> _connectionSeen = new();
    private static readonly TimeSpan PeerConfirmAge = TimeSpan.FromSeconds(4);

    public DeskflowStatus Status { get; private set; } = new();
    /// <summary>Last shared layout fetched from the layout service (null until the first successful fetch).</summary>
    public DeskflowLayoutSnapshot? Layout { get; private set; }
    public string LayoutError { get; private set; } = string.Empty;
    /// <summary>Last answer of the other computer's hub (null when unknown or unreachable).</summary>
    public PeerHubInfo? PeerHub { get; private set; }
    public string PeerHubError { get; private set; } = string.Empty;
    public string LastAction { get; private set; } = string.Empty;
    public bool IsSwitching => _switchLock.CurrentCount == 0;
    public IDeskflowHost Host => _host;
    public PeerLink Link => _link;
    public event Action? Changed;

    public DeskflowManager()
    {
        _settings = DeskflowSettings.Load();
        _settingsStamp = SettingsStamp();
        _host = OperatingSystem.IsWindows()
            ? new DeskflowWindowsHost(() => Settings, UpdateSettingsSilently, OnReloadRequested)
            : new DeskflowLinuxHost();
        _link = new PeerLink(this);
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public DeskflowSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public string ConfigPath => _host.ConfigPath;
    public string CoreLogPath => _host.CoreLogPath;
    public string InstallLocation => _host.InstallLocation;

    public static string RoleName(DeskflowRole role) => role switch
    {
        DeskflowRole.Server => "服务端",
        DeskflowRole.Client => "客户端",
        _ => "关闭"
    };

    /// <summary>This computer's Deskflow screen name ([core] computerName).</summary>
    public string LocalScreenName
    {
        get
        {
            var name = Status.ScreenName;
            if (string.IsNullOrWhiteSpace(name)) name = IniGet(ReadConf(), "core", "computerName") ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Seeds the role from Deskflow.conf on first run, starts the peer link and the 5 s guard.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        EnsureInitialized();
        var s = Settings;
        Log.Info($"Deskflow: 角色={RoleName(s.Role)}{(s.Role == DeskflowRole.Client ? " → " + s.RemoteHost : string.Empty)} 托管={(s.Managed ? "Hub" : "关闭")} 联动对端={(s.LinkPeer ? "开" : "关")} 位置={_host.InstallLocation}");
        _link.Start();
        _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    /// <summary>Stops the guard and the peer link but leaves the core running (hot replace / exit).</summary>
    public void Detach()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _link.Dispose();
        _host.Detach();
    }

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_settings.Initialized) return;
            var conf = ReadConf();
            _settings.Role = ConfRole(conf) == DeskflowRole.Client ? DeskflowRole.Client : DeskflowRole.Server;
            _settings.RemoteHost = IniGet(conf, "client", "remoteHost") ?? string.Empty;
            _settings.Initialized = true;
            SaveLocked();
        }
    }

    public void UpdateSettings(Action<DeskflowSettings> edit)
    {
        lock (_gate)
        {
            edit(_settings);
            SaveLocked();
        }
        _badTicks = 0;
        _takeOverAttempted = false;
        _repairBackoff = TimeSpan.FromSeconds(15);
        Changed?.Invoke();
    }

    private void UpdateSettingsSilently(Action<DeskflowSettings> edit)
    {
        lock (_gate)
        {
            edit(_settings);
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            _settings.Save();
            _settingsStamp = SettingsStamp();
        }
        catch (Exception ex)
        {
            Log.Warn("保存 deskflow.json 失败: " + ex.Message);
        }
    }

    private static DateTime SettingsStamp()
    {
        try { return File.Exists(DeskflowSettings.FilePath) ? File.GetLastWriteTimeUtc(DeskflowSettings.FilePath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Picks up changes another hub process (the CLI) wrote to deskflow.json.</summary>
    private void ReloadSettingsIfChanged()
    {
        var stamp = SettingsStamp();
        if (stamp == _settingsStamp) return;
        lock (_gate)
        {
            _settings = DeskflowSettings.Load();
            _settingsStamp = stamp;
        }
        _badTicks = 0;
        Log.Info($"Deskflow: 设置已在外部修改，角色={RoleName(_settings.Role)}");
    }

    // ------------------------------------------------------------------ legacy autostart pieces

    /// <summary>Switches off Deskflow's own autostart / background pieces; the hub runs the core from now on.</summary>
    public List<string> TakeOverLegacy()
    {
        var notes = _host.TakeOverLegacy(Settings);
        UpdateSettingsSilently(s => { s.LegacyTakenOver = true; s.Managed = true; });
        _badTicks = 2;
        _lastRepairUtc = DateTime.MinValue;
        Changed?.Invoke();
        return notes;
    }

    /// <summary>Rollback: stops the hub's core and gives Deskflow its own autostart pieces back.</summary>
    public List<string> RestoreLegacy()
    {
        UpdateSettingsSilently(s => s.Managed = false);
        var cores = _host.FindCores();
        if (cores.Count > 0) _host.Stop(cores);
        var notes = _host.RestoreLegacy(Settings);
        UpdateSettingsSilently(s => s.LegacyTakenOver = false);
        Changed?.Invoke();
        return notes;
    }

    public string LegacyStatus() => _host.LegacyStatus();

    // ------------------------------------------------------------------ peer address

    /// <summary>
    /// Sets the other computer's address (typed in by the user) and points every Deskflow component of this computer at
    /// it: client-mode server address, peer hub, layout service and (Windows) the side-channel programs.
    /// </summary>
    public List<string> SetPeerHost(string host)
    {
        host = host.Trim();
        if (!DeskflowPeer.IsValidHost(host)) throw new ArgumentException("地址格式不正确: " + host);
        var s = Settings;
        var wrapper = OperatingSystem.IsWindows() && s.UseTouchFilter ? DeskflowWindowsHost.WrapperExe : null;
        var notes = DeskflowPeer.ApplyHost(host, ConfigPath, wrapper, updateTasks: OperatingSystem.IsWindows());

        // the side-channel programs the hub runs carry the address in their arguments as well
        var changed = new List<SidecarSpec>();
        foreach (var spec in s.Sidecars)
        {
            var args = DeskflowPeer.RewriteArguments(spec.Arguments, host, wrapper);
            if (args == spec.Arguments) continue;
            spec.Arguments = args;
            changed.Add(spec);
        }
        lock (_gate)
        {
            _settings.RemoteHost = host;
            if (changed.Count > 0) _settings.Sidecars = s.Sidecars;
            SaveLocked();
        }
        if (_host is DeskflowWindowsHost win)
        {
            foreach (var spec in changed)
            {
                win.RestartSidecar(spec);
                notes.Add($"{spec.Name}: 已改用 {host} 并重启");
            }
        }
        Layout = null;
        PeerHub = null;
        _lastPeerStatusUtc = DateTime.MinValue;
        _ = RefreshLayoutAsync();
        Changed?.Invoke();
        return notes;
    }

    // ------------------------------------------------------------------ role switching

    /// <summary>
    /// Switches this computer to <paramref name="role"/> and, when <paramref name="cascade"/> is set and linking is on, the
    /// other computer's hub to the opposite role (the new server always starts listening before the client connects).
    /// </summary>
    public async Task<(bool Ok, string Message)> SetRoleAsync(DeskflowRole role, string? remoteHost = null, bool cascade = true)
    {
        remoteHost = remoteHost?.Trim();
        if (role == DeskflowRole.Client)
        {
            remoteHost = string.IsNullOrWhiteSpace(remoteHost) ? Settings.RemoteHost : remoteHost;
            if (string.IsNullOrWhiteSpace(remoteHost)) return (false, "客户端模式需要填写对端（服务端）IP");
            if (!DeskflowPeer.IsValidHost(remoteHost)) return (false, "服务端地址格式不正确: " + remoteHost);
            if (!string.Equals(remoteHost, Settings.RemoteHost, StringComparison.OrdinalIgnoreCase))
            {
                try { SetPeerHost(remoteHost); } catch (Exception ex) { Log.Warn("更新对端地址失败: " + ex.Message); }
            }
        }
        if (!_host.Installed) return (false, "没有安装 Deskflow（" + _host.InstallLocation + "）");

        await _switchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _settings.Role = role;
                if (role == DeskflowRole.Client) _settings.RemoteHost = remoteHost!;
                _settings.Managed = true;
                SaveLocked();
            }
            Changed?.Invoke();
            var s = Settings;
            if (!s.LegacyTakenOver && !_takeOverAttempted)
            {
                _takeOverAttempted = true;
                try { TakeOverLegacy(); } catch (Exception ex) { Log.Warn("接管 Deskflow 自启组件失败: " + ex.Message); }
                s = Settings;
            }

            var notes = new List<string>();
            var linked = cascade && s.LinkPeer && DeskflowPeer.IsValidHost(s.RemoteHost) && role != DeskflowRole.Off;
            if (linked && role == DeskflowRole.Client)
            {
                var (ok, msg) = await PeerLink.RequestRoleAsync(s.RemoteHost, DeskflowRole.Server).ConfigureAwait(false);
                notes.Add(ok ? "对端已切换为服务端" : "对端未能切换为服务端（" + msg + "）");
            }

            var (localOk, text) = await ApplyLocalAsync(s).ConfigureAwait(false);

            if (linked && role == DeskflowRole.Server && localOk)
            {
                var (ok, msg) = await PeerLink.RequestRoleAsync(s.RemoteHost, DeskflowRole.Client).ConfigureAwait(false);
                notes.Add(ok ? "对端已切换为客户端" : "对端未能切换为客户端（" + msg + "）");
                if (ok)
                {
                    // give the peer a moment to connect so the result shows it
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                    while (Status.Peers.Count == 0 && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        Refresh();
                    }
                    if (Status.Peers.Count > 0) text = $"已切换为服务端，{string.Join(", ", Status.Peers)} 已连接";
                }
            }

            _badTicks = 0;
            _repairBackoff = TimeSpan.FromSeconds(15);
            _lastPeerStatusUtc = DateTime.MinValue;
            var message = text + (notes.Count > 0 ? "；" + string.Join("；", notes) : string.Empty);
            Log.Info("Deskflow: " + message);
            LastAction = message;
            Changed?.Invoke();
            return (localOk, message);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>Writes Deskflow.conf, restarts the core in the configured role and waits until it is up.</summary>
    private async Task<(bool Ok, string Message)> ApplyLocalAsync(DeskflowSettings s)
    {
        try
        {
            WriteConfForRole(s);
        }
        catch (Exception ex)
        {
            return (false, "写入 Deskflow.conf 失败: " + ex.Message);
        }

        var cores = _host.FindCores();
        // no gap: the stand-alone touch isolator takes over before the filtered core goes away
        _host.PrepareForCoreRestart(s);
        if (cores.Count > 0) _host.Stop(cores);
        if (s.Role == DeskflowRole.Off)
        {
            var left = _host.FindCores().Count;
            _host.Tick(s, Refresh());
            return left == 0 ? (true, "Deskflow 已停止") : (false, "已请求停止，但 deskflow-core 仍在运行");
        }

        try
        {
            _host.Start(s.Role, s);
        }
        catch (Exception ex)
        {
            return (false, "启动 deskflow-core 失败: " + ex.Message);
        }
        (_host as DeskflowWindowsHost)?.ForceFilterCheck();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        DeskflowStatus st;
        do
        {
            await Task.Delay(700).ConfigureAwait(false);
            st = Refresh();
        } while (!IsRunning(s, st) && DateTime.UtcNow < deadline);

        // a client counts as connected only once its connection has lasted a few seconds
        if (s.Role == DeskflowRole.Client && IsRunning(s, st))
        {
            var connectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (st.Peers.Count == 0 && DateTime.UtcNow < connectDeadline)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                st = Refresh();
            }
        }

        var running = IsRunning(s, st);
        _host.Tick(s, st); // hand touch isolation to the filter / isolator that fits the new state right away
        return s.Role switch
        {
            DeskflowRole.Server => running
                ? (true, $"已切换为服务端（监听 {st.Port}{(st.Filtered ? "，触摸过滤已启用" : string.Empty)}）")
                : (false, "已启动服务端，但端口尚未监听"),
            _ => running
                ? (true, st.Peers.Count > 0 ? $"已切换为客户端并连接到 {st.Peers[0]}" : $"已切换为客户端，正在连接 {s.RemoteHost}:{st.Port}（对方需以服务端运行，并在布局里包含屏幕 {st.ScreenName}）")
                : (false, "已请求切换为客户端，但 deskflow-core 没有运行")
        };
    }

    /// <summary>The core is up in the wanted role (server: listening; client: process running).</summary>
    private static bool IsRunning(DeskflowSettings s, DeskflowStatus st) => s.Role switch
    {
        DeskflowRole.Server => st.RunningRole == DeskflowRole.Server && st.Listening,
        DeskflowRole.Client => st.RunningRole == DeskflowRole.Client,
        _ => st.MainCores == 0
    };

    private void WriteConfForRole(DeskflowSettings s)
    {
        var path = ConfigPath;
        var text = ReadConf();
        var updated = text;
        if (s.Role != DeskflowRole.Off) updated = IniSet(updated, "core", "coreMode", s.Role == DeskflowRole.Client ? "1" : "2");
        if (s.Role == DeskflowRole.Client) updated = IniSet(updated, "client", "remoteHost", s.RemoteHost);
        // the Deskflow GUI (if someone opens it) must never start a core of its own
        updated = IniSet(updated, "gui", "startCoreWithGui", "false");
        if (updated == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".hub.tmp";
        File.WriteAllText(tmp, updated, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>The Deskflow service pipe was asked to (re)start the core, e.g. by the layout sync after a layout change.</summary>
    private void OnReloadRequested(DeskflowRole? role)
    {
        var s = Settings;
        if (role is { } requested && requested != s.Role && requested != DeskflowRole.Off)
        {
            Log.Info($"Deskflow: 外部请求切换为{RoleName(requested)}");
            _ = SetRoleAsync(requested);
            return;
        }
        // several scripts may ask at once; one restart is enough
        if ((DateTime.UtcNow - _lastReloadRequestUtc) < TimeSpan.FromSeconds(3)) return;
        _lastReloadRequestUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000).ConfigureAwait(false);
            if (IsSwitching) return;
            await _switchLock.WaitAsync().ConfigureAwait(false);
            try { await ApplyLocalAsync(Settings).ConfigureAwait(false); }
            finally { _switchLock.Release(); }
        });
    }

    // ------------------------------------------------------------------ guard

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            ReloadSettingsIfChanged();
            var s = Settings;
            var st = Refresh();
            if ((DateTime.UtcNow - _lastLayoutFetchUtc) > TimeSpan.FromSeconds(15)) _ = RefreshLayoutAsync();
            if ((DateTime.UtcNow - _lastPeerStatusUtc) > TimeSpan.FromSeconds(15)) _ = RefreshPeerHubAsync();
            if (!s.Managed || IsSwitching || !st.Installed) return;

            if (!s.LegacyTakenOver && !_takeOverAttempted)
            {
                _takeOverAttempted = true;
                try { TakeOverLegacy(); } catch (Exception ex) { Log.Warn("接管 Deskflow 自启组件失败: " + ex.Message); }
                s = Settings;
                st = Refresh();
            }
            _host.Tick(s, st);

            if (IsHealthy(s, st) && ConfMatches(s, st))
            {
                if (_badTicks > 0) Log.Info("Deskflow: 已恢复正常 (" + st.Summary + ")");
                _badTicks = 0;
                _repairBackoff = TimeSpan.FromSeconds(15);
                return;
            }

            _badTicks++;
            if (_badTicks < 2 || (DateTime.UtcNow - _lastRepairUtc) < _repairBackoff) return;
            Log.Warn($"Deskflow: 状态异常（{Describe(s, st)}），重新启动{RoleName(s.Role)}（下次重试间隔 {_repairBackoff.TotalSeconds:0}s）");
            _lastRepairUtc = DateTime.UtcNow;
            _repairBackoff = TimeSpan.FromSeconds(Math.Min(_repairBackoff.TotalSeconds * 2, 300));
            _badTicks = 0;
            _switchLock.Wait();
            try { ApplyLocalAsync(s).GetAwaiter().GetResult(); }
            finally { _switchLock.Release(); }
        }
        catch (Exception ex)
        {
            Log.Warn("Deskflow 守护出错: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
            try { Changed?.Invoke(); } catch { }
        }
    }

    private static bool ConfMatches(DeskflowSettings s, DeskflowStatus st)
    {
        if (s.Role == DeskflowRole.Off) return true;
        if (st.ConfiguredRole != s.Role) return false;
        return s.Role != DeskflowRole.Client || string.Equals(st.RemoteHost, s.RemoteHost, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsHealthy(DeskflowSettings s, DeskflowStatus st)
    {
        switch (s.Role)
        {
            case DeskflowRole.Off:
                return st.MainCores == 0;
            case DeskflowRole.Client:
                return st.RunningRole == DeskflowRole.Client && st.MainCores == 1 && !st.Filtered;
            default:
                if (st.RunningRole != DeskflowRole.Server || !st.Listening || st.MainCores != 1) return false;
                var wantFilter = OperatingSystem.IsWindows() && s.UseTouchFilter && DeskflowWindowsHost.WrapperExe != null;
                if (wantFilter != st.Filtered) return false;
                return !st.Filtered || (st.FilterHealthy != false && st.F24Rule);
        }
    }

    private string Describe(DeskflowSettings s, DeskflowStatus st)
    {
        var parts = new List<string>();
        if (st.RunningRole != s.Role) parts.Add($"运行中={RoleName(st.RunningRole)} 期望={RoleName(s.Role)}");
        if (st.MainCores > 1) parts.Add($"有 {st.MainCores} 个 core 同时运行");
        if (!ConfMatches(s, st)) parts.Add($"配置 coreMode={RoleName(st.ConfiguredRole)}{(s.Role == DeskflowRole.Client ? " remoteHost=" + st.RemoteHost : string.Empty)}");
        if (s.Role == DeskflowRole.Server)
        {
            if (!st.Listening) parts.Add($"端口 {st.Port} 未监听");
            if (OperatingSystem.IsWindows() && s.UseTouchFilter && DeskflowWindowsHost.WrapperExe != null && !st.Filtered) parts.Add("未经过触摸过滤");
            if (st.FilterHealthy == false) parts.Add("触摸过滤不健康");
            if (st.Filtered && !st.F24Rule) parts.Add("缺少 F24 规则");
        }
        return parts.Count == 0 ? "未知" : string.Join("，", parts);
    }

    /// <summary>Takes a fresh status snapshot (processes, connections, config).</summary>
    public DeskflowStatus Refresh()
    {
        var conf = ReadConf();
        var port = int.TryParse(IniGet(conf, "core", "port"), out var p) ? p : 24800;
        var cores = _host.FindCores();
        var wrappers = cores.Where(c => c.IsWrapper).ToList();
        var main = wrappers.FirstOrDefault() ?? cores.FirstOrDefault();
        var role = main == null ? DeskflowRole.Off : main.Role;
        var mainCores = wrappers.Count + Math.Max(0, cores.Count - 2 * wrappers.Count);
        var (listening, connections) = _host.Connections(port, role, cores.Select(c => c.Pid).ToHashSet());

        var peers = new List<string>();
        var now = DateTime.UtcNow;
        lock (_connectionSeen)
        {
            var keys = connections.Select(c => c.Key).ToHashSet();
            foreach (var stale in _connectionSeen.Keys.Where(k => !keys.Contains(k)).ToList()) _connectionSeen.Remove(stale);
            foreach (var c in connections)
            {
                if (!_connectionSeen.TryGetValue(c.Key, out var first)) _connectionSeen[c.Key] = first = now;
                if (now - first >= PeerConfirmAge && !peers.Contains(c.RemoteAddress)) peers.Add(c.RemoteAddress);
            }
        }

        var filtered = wrappers.Count > 0;
        var st = new DeskflowStatus
        {
            Installed = _host.Installed,
            ConfiguredRole = ConfRole(conf),
            RunningRole = role,
            CorePid = cores.FirstOrDefault(c => !c.IsWrapper)?.Pid ?? main?.Pid,
            MainCores = mainCores,
            Filtered = filtered,
            FilterHealthy = role == DeskflowRole.Server && filtered ? _host.CheckFilter(cores) : null,
            F24Rule = _host.F24RulePresent(),
            Listening = listening,
            Peers = peers,
            MonitorRunning = _host.MonitorRunning(),
            TouchBridgeRunning = _host.TouchBridgeRunning(),
            RemoteHost = IniGet(conf, "client", "remoteHost") ?? string.Empty,
            ScreenName = IniGet(conf, "core", "computerName") ?? Environment.MachineName,
            Port = port,
            Tls = IniBool(conf, "security", "tlsEnabled"),
        };
        Status = st;
        return st;
    }

    public static DeskflowRole ParseRole(string cmd)
    {
        var m = Regex.Match(cmd, @"(?:^|\s|"")(server|client)(?:\s|$)", RegexOptions.IgnoreCase);
        if (!m.Success) return DeskflowRole.Server;
        return m.Groups[1].Value.Equals("client", StringComparison.OrdinalIgnoreCase) ? DeskflowRole.Client : DeskflowRole.Server;
    }

    // ------------------------------------------------------------------ shared layout and peer hub

    /// <summary>Server address of the shared layout service as seen from this computer.</summary>
    public string LayoutHost => _host.LayoutHost(Settings.RemoteHost);

    /// <summary>Fetches the shared screen layout from the layout service so the display views can draw it.</summary>
    public async Task RefreshLayoutAsync()
    {
        var host = LayoutHost;
        if (!DeskflowPeer.IsValidHost(host)) { LayoutError = "未设置对端 IP"; return; }
        if (Interlocked.Exchange(ref _layoutFetching, 1) == 1) return;
        try
        {
            _lastLayoutFetchUtc = DateTime.UtcNow;
            Layout = await DeskflowPeer.GetLayoutAsync(host, LocalScreenName).ConfigureAwait(false);
            LayoutError = Layout.HasLocalScreen ? string.Empty : $"共享布局里没有本机屏幕「{LocalScreenName}」";
            SaveLayoutCache(Layout);
        }
        catch (Exception ex)
        {
            // the layout service only exists on the Linux side: while the other computer runs Windows (dual boot) or is
            // off, keep drawing the last known layout so its screen can still be placed (see ApplyPeerEdgesLocally)
            Layout ??= LoadLayoutCache();
            LayoutError = $"无法读取 {host}:{DeskflowPeer.LayoutPort} 的共享布局: {ex.GetBaseException().Message}";
            if (Layout != null && DeskflowPeer.IsUnreachable(ex))
            {
                LayoutError = $"布局服务 {host}:{DeskflowPeer.LayoutPort} 不可用（对端可能正运行 Windows 或没开机），显示的是上次的布局"
                    + (CanLinkLocally ? "；「应用摆放」会直接修改本机 Deskflow 服务端配置" : string.Empty);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _layoutFetching, 0);
            try { Changed?.Invoke(); } catch { }
        }
    }

    private static string LayoutCachePath => Path.Combine(Paths.DataDir, "deskflow-layout-cache.json");

    private void SaveLayoutCache(DeskflowLayoutSnapshot snapshot)
    {
        try
        {
            var text = new JsonObject { ["server"] = snapshot.Server, ["layout"] = snapshot.Layout.DeepClone() }.ToJsonString();
            if (File.Exists(LayoutCachePath) && File.ReadAllText(LayoutCachePath) == text) return;
            File.WriteAllText(LayoutCachePath, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Debug("保存 Deskflow 布局缓存失败: " + ex.Message);
        }
    }

    /// <summary>The copy of the shared layout the layout sync task keeps on this PC (its --layout-json), same shape as /api/layout's "layout".</summary>
    private static string SyncedLayoutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "deskflow-layout", "layout.json");

    private DeskflowLayoutSnapshot? LoadLayoutCache()
    {
        try
        {
            JsonObject? layout = null;
            var server = string.Empty;
            if (File.Exists(LayoutCachePath) && JsonNode.Parse(File.ReadAllText(LayoutCachePath)) is JsonObject root && root["layout"] is JsonObject cached)
            {
                layout = cached;
                server = root["server"]?.GetValue<string>() ?? string.Empty;
            }
            else if (File.Exists(SyncedLayoutPath) && JsonNode.Parse(File.ReadAllText(SyncedLayoutPath)) is JsonObject synced && synced["screens"] is JsonObject)
            {
                layout = synced;
                server = synced["server"]?.GetValue<string>() ?? string.Empty;
            }
            if (layout == null) return null;
            var local = LocalScreenName;
            var screens = layout["screens"] as JsonObject;
            var name = screens?.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(local, StringComparison.OrdinalIgnoreCase)) ?? local;
            return new DeskflowLayoutSnapshot { Layout = (JsonObject)layout.DeepClone(), Server = server, LocalScreen = name, PeerStatus = "cached" };
        }
        catch (Exception ex)
        {
            Log.Debug("读取 Deskflow 布局缓存失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>This computer can place peer screens without the layout service: it is the Deskflow server on Windows.</summary>
    public bool CanLinkLocally => OperatingSystem.IsWindows() && Settings.Role == DeskflowRole.Server;

    /// <summary>
    /// "Apply layout" while the shared layout service is unreachable (it runs on the Linux side only, so it is gone
    /// whenever the other computer is booted into Windows): writes which side of the local desktop each peer screen sits
    /// on straight into the Deskflow server config and restarts the core (the touch filter wrapper re-reads the config
    /// on every start). The layout sync task leaves the file alone while the service is down and replaces it with the
    /// shared layout once the service is back.
    /// </summary>
    public string ApplyPeerEdgesLocally(IReadOnlyCollection<LocalRect> localDisplays, IReadOnlyCollection<(string Name, int X, int Y)> peers)
    {
        if (!CanLinkLocally) throw new InvalidOperationException("本机不是 Deskflow 服务端，无法在本机修改屏幕位置");
        var snapshot = Layout ?? throw new InvalidOperationException("还没有读到过 Deskflow 布局，不知道对端屏幕的大小");
        var sizes = DeskflowPeer.MapPeers(snapshot, localDisplays).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var links = new List<(string Peer, string Side)>();
        foreach (var (name, x, y) in peers)
        {
            if (!sizes.TryGetValue(name, out var size)) continue;
            var side = DeskflowPeer.SideOf(localDisplays, x, y, size.Width, size.Height)
                ?? throw new InvalidOperationException($"{name} 与本机显示器重叠，请把它拖到某条边的外侧");
            links.Add((name, side));
        }
        if (links.Count == 0) throw new InvalidOperationException("没有可摆放的对端屏幕");

        var path = DeskflowWindowsHost.SharedServerConfig;
        var old = File.ReadAllText(path);
        var updated = DeskflowPeer.LinkPeersInServerConfig(old, LocalScreenName, links);
        string SideName(string s) => s switch { "left" => "左侧", "right" => "右侧", "up" => "上方", _ => "下方" };
        var summary = string.Join("、", links.Select(l => $"{l.Peer} 在{SideName(l.Side)}"));
        if (updated != old)
        {
            File.Copy(path, path + ".bak-hub-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            var tmp = path + ".hub.tmp";
            File.WriteAllText(tmp, updated, new UTF8Encoding(false));
            File.Move(tmp, path, true);
            Log.Info($"Deskflow 布局服务不可用，直接写入本机服务端配置: {summary}");
            OnReloadRequested(null); // restart the core so it reads the new links
        }
        DeskflowPeer.MovePeers(snapshot, localDisplays, peers);
        SaveLayoutCache(snapshot);
        return updated != old
            ? $"布局服务不可用，已直接修改本机 Deskflow 服务端配置：{summary}（Deskflow 正在重启）"
            : $"本机 Deskflow 服务端配置已经是：{summary}";
    }

    private async Task RefreshPeerHubAsync()
    {
        var s = Settings;
        if (!s.LinkPeer || !DeskflowPeer.IsValidHost(s.RemoteHost)) { PeerHub = null; PeerHubError = s.LinkPeer ? "未设置对端 IP" : "联动已关闭"; return; }
        if (Interlocked.Exchange(ref _peerFetching, 1) == 1) return;
        try
        {
            _lastPeerStatusUtc = DateTime.UtcNow;
            PeerHub = await PeerLink.GetStatusAsync(s.RemoteHost, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            PeerHubError = string.Empty;
        }
        catch (Exception ex)
        {
            PeerHub = null;
            PeerHubError = $"{s.RemoteHost}:{PeerLink.Port} 上没有可联动的 Moonlight Hub（{ex.GetBaseException().Message}）";
        }
        finally
        {
            Interlocked.Exchange(ref _peerFetching, 0);
        }
    }

    /// <summary>Last lines of the core's log file.</summary>
    public string TailCoreLog(int lines = 60)
    {
        var path = CoreLogPath;
        try
        {
            if (!File.Exists(path)) return "（还没有日志：" + path + "）";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - 32 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - lines)));
        }
        catch (Exception ex)
        {
            return "读取日志失败: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ Deskflow.conf (QSettings INI)

    private string ReadConf()
    {
        try
        {
            return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static DeskflowRole ConfRole(string conf) => IniGet(conf, "core", "coreMode") switch
    {
        "1" => DeskflowRole.Client,
        "2" => DeskflowRole.Server,
        _ => DeskflowRole.Off
    };

    private static bool IniBool(string conf, string section, string key) =>
        (IniGet(conf, section, key) ?? string.Empty).Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on";

    public static string? IniGet(string text, string section, string key)
    {
        var current = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1].Trim(); continue; }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(idx + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Sets one key, keeping every other line (and the file's line endings) untouched.</summary>
    public static string IniSet(string text, string section, string key, string value)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var current = string.Empty;
        var sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) break;
                current = line[1..^1].Trim();
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionEnd = i + 1;
                continue;
            }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length > 0) sectionEnd = i + 1;
            var idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (line[(idx + 1)..].Trim() == value) return text;
                lines[i] = key + "=" + value;
                return string.Join(nl, lines) + nl;
            }
        }
        if (sectionEnd >= 0)
        {
            lines.Insert(sectionEnd, key + "=" + value);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
        }
        return string.Join(nl, lines) + nl;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _link.Dispose();
    }
}

/// <summary>Image path and command line of another process (no WMI; Windows only).</summary>
internal static class NativeProcessInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr info, int length, out int returned);

    public static (string? Path, string? CommandLine) Query(int pid)
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size)) path = sb.ToString(0, size);

            string? cmd = null;
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed > 0 && needed < 1 << 20)
            {
                var buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, needed, out _) == 0)
                    {
                        var length = (ushort)Marshal.ReadInt16(buf);
                        var ptr = Marshal.ReadIntPtr(buf, IntPtr.Size); // UNICODE_STRING { USHORT Length; USHORT Max; PWSTR Buffer }
                        if (ptr != IntPtr.Zero && length > 0) cmd = Marshal.PtrToStringUni(ptr, length / 2);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return (path, cmd);
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
