using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>
/// Windows: the hub starts deskflow-core itself (through the touch filter wrapper in server mode) with the user's
/// normal token, instead of the Deskflow service. Deskflow's own autostart pieces (service, GUI, priority / keepalive /
/// touch-guard tasks) are switched off, the side-channel scripts formerly started by scheduled tasks run under the hub's
/// supervision, and the hub answers on the Deskflow service's pipe so existing scripts can still ask for a reload.
/// </summary>
internal sealed class DeskflowWindowsHost : IDeskflowHost
{
    public const string ServiceName = "Deskflow";
    private const string PipeName = "deskflow-daemon";

    /// <summary>Deskflow autostart tasks the hub replaces completely.</summary>
    public static readonly string[] ReplacedTasks =
    {
        "DeskflowGui",
        "Deskflow Core Priority",
        "Deskflow Low Latency Keepalive",
        "Moonlight Deskflow Touch Guard",
    };

    private readonly Func<DeskflowSettings> _settings;
    private readonly Action<Action<DeskflowSettings>> _update;
    private readonly Action<DeskflowRole?> _reloadRequested;
    private DateTime _lastFilterCheckUtc = DateTime.MinValue;
    private bool? _lastFilterHealthy;
    private DateTime _lastMonitorStartUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _sidecarStarts = new();
    private readonly Dictionary<string, int> _sidecarPids = new();
    private Thread? _pipeThread;
    private volatile bool _pipeStop;
    private string _pendingCommand = string.Empty;

    /// <param name="settings">current Deskflow settings.</param>
    /// <param name="update">persists a settings change.</param>
    /// <param name="reloadRequested">a client of the emulated service pipe asked to (re)start the core (role parsed from its command, null = stop).</param>
    public DeskflowWindowsHost(Func<DeskflowSettings> settings, Action<Action<DeskflowSettings>> update, Action<DeskflowRole?> reloadRequested)
    {
        _settings = settings;
        _update = update;
        _reloadRequested = reloadRequested;
    }

    // ------------------------------------------------------------------ paths

    public string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Deskflow", "Deskflow.conf");
    public string CoreLogPath => Path.Combine(Paths.LogDir, "deskflow-core.log");
    public static string BridgeRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoonlightDeskflowBridge");
    public static string PrivateServerConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskflowTouchFilter", "server.conf");
    public static string SharedServerConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Deskflow", "deskflow-server.conf");

    public string InstallDir
    {
        get
        {
            var configured = _settings().InstallDir;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "deskflow-core.exe"))) return configured;
            foreach (var dir in new[]
                     {
                         @"D:\YingYong\Deskflow",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Deskflow"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Deskflow"),
                     })
            {
                if (File.Exists(Path.Combine(dir, "deskflow-core.exe"))) return dir;
            }
            return configured;
        }
    }

    public string CoreExe => Path.Combine(InstallDir, "deskflow-core.exe");
    public string GuiExe => Path.Combine(InstallDir, "deskflow.exe");
    public bool Installed => File.Exists(CoreExe);
    public string InstallLocation => InstallDir;
    public string LayoutHost(string peerHost) => peerHost;

    /// <summary>Newest touch filter runtime (…\MoonlightDeskflowBridge\v&lt;n&gt;) that has the wrapper core.</summary>
    public static string? BridgeRuntimeDir
    {
        get
        {
            try
            {
                if (!Directory.Exists(BridgeRoot)) return null;
                return Directory.GetDirectories(BridgeRoot, "v*")
                    .Select(d => (Dir: d, N: int.TryParse(Path.GetFileName(d)[1..], out var n) ? n : -1))
                    .Where(t => t.N >= 0 && File.Exists(Path.Combine(t.Dir, "deskflow-core.exe")))
                    .OrderByDescending(t => t.N)
                    .Select(t => t.Dir)
                    .FirstOrDefault();
            }
            catch { return null; }
        }
    }

    public static string? WrapperExe => BridgeRuntimeDir is { } d ? Path.Combine(d, "deskflow-core.exe") : null;
    public static string? MonitorExe => BridgeRuntimeDir is { } d && File.Exists(Path.Combine(d, "moonlight-touch-keyboard-release.exe")) ? Path.Combine(d, "moonlight-touch-keyboard-release.exe") : null;

    // ------------------------------------------------------------------ core processes

    public List<CoreProcess> FindCores()
    {
        var list = new List<CoreProcess>();
        var wrapper = WrapperExe;
        foreach (var proc in Process.GetProcessesByName("deskflow-core"))
        {
            using (proc)
            {
                var (path, cmd) = NativeProcessInfo.Query(proc.Id);
                cmd ??= string.Empty;
                if (cmd.Contains("--touch-filter-", StringComparison.OrdinalIgnoreCase)) continue; // status probes
                var isWrapper = wrapper != null && string.Equals(path, wrapper, StringComparison.OrdinalIgnoreCase);
                list.Add(new CoreProcess(proc.Id, DeskflowManager.ParseRole(cmd), isWrapper, path ?? string.Empty, cmd));
            }
        }
        return list;
    }

    public int Start(DeskflowRole role, DeskflowSettings settings)
    {
        if (!Installed) throw new InvalidOperationException("找不到 deskflow-core.exe（" + InstallDir + "）");
        EnsureLogConfig();
        var filtered = role == DeskflowRole.Server && settings.UseTouchFilter && WrapperExe != null;
        var exe = filtered ? WrapperExe! : CoreExe;
        var mode = role == DeskflowRole.Server ? "server" : "client";
        var args = $"{mode} --settings \"{ConfigPath}\"";
        // A client injects the server's mouse and keyboard into this desktop. Started with the shell's (medium) token,
        // UIPI rejects every injected event while an elevated window has the focus — this hub's own window, an admin
        // terminal — and the cursor just stands still (2026-10-09, qingxian on Windows). The elevated hub therefore starts
        // the client with its own token; the server keeps the shell token (the touch filter and the medium-IL touch
        // route mapping need it, and capturing input through low-level hooks works at any level).
        var pid = role == DeskflowRole.Client && ProcessUtil.IsElevated() ? StartWithOwnToken(exe, args) : ProcessUtil.StartAsShellUserPid(exe, args, hidden: true);
        if (pid <= 0) throw new InvalidOperationException("无法启动 " + exe);
        Log.Info($"Deskflow: 已启动 {(filtered ? "触摸过滤包装器" : "deskflow-core")} {mode} (pid {pid})");
        return pid;
    }

    private static int StartWithOwnToken(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty });
            return p?.Id ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"启动 {exe} 失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The core logs to a file of the hub (it no longer runs under the service, which used to collect its output).</summary>
    private void EnsureLogConfig()
    {
        try
        {
            var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;
            var updated = DeskflowManager.IniSet(text, "log", "toFile", "true");
            updated = DeskflowManager.IniSet(updated, "log", "file", CoreLogPath.Replace('\\', '/'));
            if (updated != text) File.WriteAllText(ConfigPath, updated, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("写入 Deskflow 日志设置失败: " + ex.Message);
        }
    }

    public void Stop(IReadOnlyCollection<CoreProcess> cores)
    {
        // the wrapper first (it would restart its child), then whatever is left
        foreach (var core in cores.OrderByDescending(c => c.IsWrapper))
        {
            try
            {
                using var p = Process.GetProcessById(core.Pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    public (bool Listening, List<CoreConnection> Connections) Connections(int port, DeskflowRole role, IReadOnlyCollection<int> corePids)
    {
        var listening = false;
        var list = new List<CoreConnection>();
        try
        {
            if (role == DeskflowRole.Server) listening = TcpTable.GetListeningPid(port) is { } lp && corePids.Contains(lp);
            foreach (var c in TcpTable.EnumerateConnections())
            {
                if (!c.Established || !corePids.Contains(c.Pid) || TcpTable.IsLocal(c.RemoteAddress)) continue;
                var mine = role == DeskflowRole.Server ? c.LocalPort == port : c.RemotePort == port;
                if (mine) list.Add(new CoreConnection($"{c.Pid}:{c.LocalPort}:{c.RemoteAddress}:{c.RemotePort}", c.RemoteAddress));
            }
        }
        catch { }
        return (listening, list);
    }

    public bool? CheckFilter(IReadOnlyCollection<CoreProcess> cores)
    {
        var wrapper = WrapperExe;
        if (wrapper == null || !cores.Any(c => c.IsWrapper)) return null;
        if ((DateTime.UtcNow - _lastFilterCheckUtc) < TimeSpan.FromSeconds(30)) return _lastFilterHealthy;
        _lastFilterCheckUtc = DateTime.UtcNow;
        try
        {
            var (code, output) = ProcessUtil.Run(wrapper, "--touch-filter-status", 5000);
            _lastFilterHealthy = code == 0 && output.Contains("filter_installed=1") && output.Contains("mouse_filter=1") && output.Contains("keyboard_filter=1");
        }
        catch { _lastFilterHealthy = false; }
        return _lastFilterHealthy;
    }

    public void ForceFilterCheck() => _lastFilterCheckUtc = DateTime.MinValue;

    public bool F24RulePresent()
    {
        try
        {
            return File.Exists(PrivateServerConfig) && Regex.IsMatch(File.ReadAllText(PrivateServerConfig), @"(?im)^\s*keystroke\(f24\)\s*=\s*switchToScreen\(");
        }
        catch { return false; }
    }

    public bool MonitorRunning() => AnyProcess("moonlight-touch-keyboard-release");

    /// <summary>Stand-alone touch isolator shipped with the hub (touch_mouse_isolator.cpp built as "moonlight-touch-bridge.exe").</summary>
    public static string BridgeExe => Path.Combine(Paths.ExeDir, "tools", "moonlight-touch-bridge.exe");
    public bool TouchBridgeRunning() => AnyProcess("moonlight-touch-bridge");

    private DateTime _lastBridgeStartUtc = DateTime.MinValue;

    /// <summary>
    /// The keyboard-release monitor and the bridge share one singleton and stop event, so only one runs at a time:
    /// "stop" (sent by the elevated hub, which may open either one's objects) ends whichever is running.
    /// </summary>
    private void StopTouchHelper(string processName)
    {
        if (!AnyProcess(processName)) return;
        var exe = File.Exists(BridgeExe) ? BridgeExe : MonitorExe;
        if (exe != null) ProcessUtil.Run(exe, "stop", 5000);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && AnyProcess(processName)) Thread.Sleep(100);
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p) { try { p.Kill(); } catch { } }
        }
    }

    /// <summary>Runs the bridge (touch never moves the real mouse) unless it already runs.</summary>
    private void EnsureBridge()
    {
        if (TouchBridgeRunning()) return;
        if (!File.Exists(BridgeExe)) return;
        if ((DateTime.UtcNow - _lastBridgeStartUtc) < TimeSpan.FromSeconds(10)) return;
        StopTouchHelper("moonlight-touch-keyboard-release");
        _lastBridgeStartUtc = DateTime.UtcNow;
        var ok = ProcessUtil.StartAsShellUser(BridgeExe, "bridge", hidden: true);
        Log.Info("Deskflow: 触摸隔离器（桥接模式）" + (ok ? "已启动：平板触摸不再移动本机鼠标" : "启动失败"));
    }

    public void PrepareForCoreRestart(DeskflowSettings settings)
    {
        if (settings.TouchIsolation) EnsureBridge();
    }

    /// <summary>
    /// Touch isolation for the current state: a filtered server core isolates touch itself (plus the keyboard-release
    /// monitor); any other state gets the stand-alone bridge.
    /// </summary>
    private void EnsureTouchIsolation(DeskflowSettings settings, DeskflowStatus status)
    {
        var filteredServer = status.RunningRole == DeskflowRole.Server && status.Filtered && status.Listening && status.FilterHealthy != false;
        if (filteredServer)
        {
            if (TouchBridgeRunning())
            {
                StopTouchHelper("moonlight-touch-bridge");
                Log.Info("Deskflow: 服务端的触摸过滤器已接管触摸，停止独立触摸隔离器");
                _lastMonitorStartUtc = DateTime.MinValue;
            }
            return;
        }
        if (settings.TouchIsolation) EnsureBridge();
        else if (TouchBridgeRunning()) StopTouchHelper("moonlight-touch-bridge");
    }

    private static bool AnyProcess(string name)
    {
        var list = Process.GetProcessesByName(name);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    // ------------------------------------------------------------------ upkeep

    public void Tick(DeskflowSettings settings, DeskflowStatus status)
    {
        // what the "Deskflow Core Priority" task used to do
        foreach (var p in Process.GetProcessesByName("deskflow-core"))
        {
            using (p)
            {
                try { if (p.PriorityClass != ProcessPriorityClass.AboveNormal) p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            }
        }

        EnsureTouchIsolation(settings, status);

        if (status.RunningRole == DeskflowRole.Server && status.Filtered && settings.KeyboardReleaseMonitor && MonitorExe is { } monitor
            && !MonitorRunning() && !TouchBridgeRunning()
            && (DateTime.UtcNow - _lastMonitorStartUtc) > TimeSpan.FromSeconds(30))
        {
            _lastMonitorStartUtc = DateTime.UtcNow;
            var ok = ProcessUtil.StartAsShellUser(monitor, "keyboard-release", hidden: true);
            Log.Info("Deskflow: 启动触摸释放键盘监视器 " + (ok ? "成功" : "失败"));
        }

        if (settings.LegacyTakenOver)
        {
            SuperviseSidecars(settings);
            EnsurePipe();
        }
    }

    // ------------------------------------------------------------------ side-channel processes (formerly scheduled tasks)

    /// <summary>Command line fragments that identify a running side-channel process started from <paramref name="spec"/>.</summary>
    private static (string Script, string? Mode) Identity(SidecarSpec spec)
    {
        var m = Regex.Match(spec.Arguments, @"^\s*(""[^""]+""|\S+)\s+(\S+)?");
        var script = m.Success ? m.Groups[1].Value.Trim('"') : spec.Arguments;
        var mode = m.Success && m.Groups[2].Success && !m.Groups[2].Value.StartsWith("--") ? m.Groups[2].Value : null;
        return (Path.GetFileName(script), mode);
    }

    private static bool Matches(SidecarSpec spec, string commandLine)
    {
        var (script, mode) = Identity(spec);
        if (!commandLine.Contains(script, StringComparison.OrdinalIgnoreCase)) return false;
        return mode == null || Regex.IsMatch(commandLine, $@"{Regex.Escape(script)}""?\s+{Regex.Escape(mode)}(\s|$)", RegexOptions.IgnoreCase);
    }

    /// <summary>Running processes per side-channel spec name.</summary>
    public Dictionary<string, int> RunningSidecars(IEnumerable<SidecarSpec> specs)
    {
        var result = new Dictionary<string, int>();
        var candidates = new List<(int Pid, string Cmd)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("powershell", StringComparison.OrdinalIgnoreCase)) continue;
                var (_, cmd) = NativeProcessInfo.Query(p.Id);
                if (!string.IsNullOrEmpty(cmd)) candidates.Add((p.Id, cmd));
            }
        }
        foreach (var spec in specs)
        {
            var hit = candidates.FirstOrDefault(c => Matches(spec, c.Cmd));
            if (hit.Pid != 0) result[spec.Name] = hit.Pid;
        }
        return result;
    }

    private DateTime _lastSidecarHealthUtc = DateTime.MinValue;

    /// <summary>Control port of a clipboard / file agent ("--control-port N"; not "--file-control-port"), or 0.</summary>
    private static (int Port, bool FileAgent) ControlPort(SidecarSpec spec)
    {
        var m = Regex.Match(spec.Arguments, @"(?:^|\s)--control-port\s+(\d+)");
        if (!m.Success) return (0, false);
        return (int.Parse(m.Groups[1].Value), Regex.IsMatch(spec.Arguments, @"(?:^|\s|"")file-agent(\s|$)"));
    }

    private DateTime _lastSidecarScanUtc = DateTime.MinValue;

    private void SuperviseSidecars(DeskflowSettings settings)
    {
        var specs = settings.Sidecars.Where(s => s.Enabled).ToList();
        if (specs.Count == 0) return;
        // enumerating every process to find the scripts is the most expensive part of the tick: every 15 s is enough
        if ((DateTime.UtcNow - _lastSidecarScanUtc) < TimeSpan.FromSeconds(15)) return;
        _lastSidecarScanUtc = DateTime.UtcNow;
        var running = RunningSidecars(specs);
        // what the "Deskflow Low Latency Keepalive" task did: an agent that is up but no longer connected / looping gets restarted
        var checkHealth = (DateTime.UtcNow - _lastSidecarHealthUtc) >= TimeSpan.FromSeconds(14);
        if (checkHealth) _lastSidecarHealthUtc = DateTime.UtcNow;
        foreach (var spec in specs)
        {
            if (running.TryGetValue(spec.Name, out var pid))
            {
                _sidecarPids[spec.Name] = pid;
                var (port, fileAgent) = ControlPort(spec);
                if (checkHealth && port > 0)
                {
                    var (healthy, detail) = SideChannelHealth.Check(port, fileAgent);
                    if (SideChannelHealth.NeedsRestart(spec.Name, healthy))
                    {
                        Log.Warn($"Deskflow 旁路: {spec.Name} 连续 3 次健康检查失败（{detail}），重启");
                        RestartSidecar(spec);
                    }
                }
                continue;
            }
            if (_sidecarStarts.TryGetValue(spec.Name, out var last) && (DateTime.UtcNow - last) < TimeSpan.FromSeconds(30)) continue;
            _sidecarStarts[spec.Name] = DateTime.UtcNow;
            var started = ProcessUtil.StartAsShellUserPid(spec.Exe, spec.Arguments, hidden: true);
            _sidecarPids[spec.Name] = started;
            Log.Info($"Deskflow 旁路: {(started > 0 ? "已启动" : "启动失败")} {spec.Name}{(started > 0 ? $" (pid {started})" : string.Empty)}");
        }
    }

    /// <summary>Stops a side-channel process so the supervisor restarts it with its new arguments.</summary>
    public void RestartSidecar(SidecarSpec spec)
    {
        foreach (var (name, pid) in RunningSidecars(new[] { spec }))
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { }
        }
        _sidecarStarts.Remove(spec.Name);
    }

    /// <summary>Reads the program and arguments of the side-channel tasks so the hub can run them itself.</summary>
    private static List<SidecarSpec> ReadSidecarTasks()
    {
        var list = new List<SidecarSpec>();
        foreach (var task in DeskflowPeer.PeerTasks)
        {
            var (code, json) = PowerShell($"$a = (Get-ScheduledTask -TaskName {PsQuote(task)} -ErrorAction Stop).Actions[0]; [pscustomobject]@{{ Execute = $a.Execute; Arguments = $a.Arguments }} | ConvertTo-Json -Compress");
            if (code != 0 || !json.TrimStart().StartsWith('{')) continue;
            var node = JsonNode.Parse(json.Trim())!;
            var exe = Environment.ExpandEnvironmentVariables(node["Execute"]?.GetValue<string>() ?? string.Empty).Trim('"');
            var args = node["Arguments"]?.GetValue<string>() ?? string.Empty;
            if (exe.Length == 0) continue;
            list.Add(new SidecarSpec { Name = task, TaskName = task, Exe = exe, Arguments = args, Enabled = true });
        }
        return list;
    }

    // ------------------------------------------------------------------ legacy autostart pieces

    public List<string> TakeOverLegacy(DeskflowSettings settings)
    {
        var notes = new List<string>();

        // 1. the side-channel scripts keep running, but under the hub instead of the Task Scheduler
        if (settings.Sidecars.Count == 0)
        {
            var sidecars = ReadSidecarTasks();
            if (sidecars.Count > 0)
            {
                _update(s => s.Sidecars = sidecars);
                notes.Add("旁路进程改由 Hub 托管: " + string.Join("、", sidecars.Select(s => s.Name)));
            }
        }
        foreach (var task in DeskflowPeer.PeerTasks)
        {
            var t = TaskSchedulerHelper.Query(task);
            if (t.Exists && !IsDisabled(t))
            {
                var dis = TaskSchedulerHelper.Disable(task); // the running instance is adopted, not ended
                notes.Add($"{task}: 禁用={(dis.Ok ? "ok" : dis.Message.Trim())}");
            }
        }

        // 2. autostart / background pieces the hub replaces entirely
        foreach (var task in ReplacedTasks)
        {
            var t = TaskSchedulerHelper.Query(task);
            if (!t.Exists || IsDisabled(t)) continue;
            var end = TaskSchedulerHelper.End(task);
            var dis = TaskSchedulerHelper.Disable(task);
            notes.Add($"{task}: 结束={(end.Ok ? "ok" : "跳过")} 禁用={(dis.Ok ? "ok" : dis.Message.Trim())}");
        }
        foreach (var gui in Process.GetProcessesByName("deskflow"))
        {
            using (gui)
            {
                try { gui.Kill(); notes.Add("已关闭 Deskflow 界面"); } catch { }
            }
        }

        // 3. the Deskflow service: clear its remembered command (so it never starts a second core) and stop it
        if (ServiceState() is { } state)
        {
            if (state == "RUNNING")
            {
                var (ok, msg) = SendDaemon("stop");
                notes.Add("Deskflow 服务: 清除启动命令 " + (ok ? "ok" : msg));
            }
            var cfg = ProcessUtil.Run("sc.exe", $"config {ServiceName} start= demand", 15000);
            var stop = state == "RUNNING" ? ProcessUtil.Run("sc.exe", $"stop {ServiceName}", 15000) : (0, string.Empty);
            notes.Add($"Deskflow 服务: 开机自启→手动 {(cfg.ExitCode == 0 ? "ok" : cfg.Output.Trim())}" + (state == "RUNNING" ? $"，停止 {(stop.Item1 == 0 ? "ok" : stop.Item2.Trim())}" : string.Empty));
            WaitForServiceStopped(TimeSpan.FromSeconds(10));
        }

        _update(s => s.LegacyTakenOver = true);
        EnsurePipe();
        Log.Info("Deskflow: 已接管开机自启与后台组件 — " + string.Join("；", notes));
        return notes;
    }

    public List<string> RestoreLegacy(DeskflowSettings settings)
    {
        var notes = new List<string>();
        StopPipe();
        foreach (var task in ReplacedTasks.Concat(DeskflowPeer.PeerTasks))
        {
            if (!TaskSchedulerHelper.Query(task).Exists) continue;
            var en = TaskSchedulerHelper.Enable(task);
            notes.Add($"{task}: 启用={(en.Ok ? "ok" : en.Message.Trim())}");
        }
        if (ServiceState() != null)
        {
            var cfg = ProcessUtil.Run("sc.exe", $"config {ServiceName} start= auto", 15000);
            var start = ProcessUtil.Run("sc.exe", $"start {ServiceName}", 15000);
            notes.Add($"Deskflow 服务: 自启={(cfg.ExitCode == 0 ? "ok" : cfg.Output.Trim())} 启动={(start.ExitCode == 0 ? "ok" : start.Output.Trim())}");
        }
        foreach (var task in ReplacedTasks.Concat(DeskflowPeer.PeerTasks).Where(t => t != "DeskflowGui"))
        {
            if (TaskSchedulerHelper.Query(task).Exists) TaskSchedulerHelper.Run(task);
        }
        _update(s => s.LegacyTakenOver = false);
        Log.Info("Deskflow: 已恢复原来的开机自启组件 — " + string.Join("；", notes));
        return notes;
    }

    public string LegacyStatus()
    {
        var service = ServiceState() switch
        {
            null => "Deskflow 服务: 未安装",
            var s => $"Deskflow 服务: {s}，启动类型 {ServiceStartType()}"
        };
        var tasks = ReplacedTasks.Concat(DeskflowPeer.PeerTasks)
            .Select(n => (n, TaskSchedulerHelper.Query(n)))
            .Where(t => t.Item2.Exists)
            .Select(t => $"{t.n}: {t.Item2.Status}");
        return service + "\n" + string.Join("\n", tasks);
    }

    private static bool IsDisabled(TaskSchedulerHelper.TaskInfo t) =>
        t.Status.Contains("Disabled", StringComparison.OrdinalIgnoreCase) || t.Status.Contains("禁用");

    /// <summary>RUNNING / STOPPED / … of the Deskflow service, null when it is not installed.</summary>
    public static string? ServiceState()
    {
        var (code, output) = ProcessUtil.Run("sc.exe", $"query {ServiceName}", 10000);
        if (code != 0) return null;
        var m = Regex.Match(output, @"STATE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value : "UNKNOWN";
    }

    private static string ServiceStartType()
    {
        var (code, output) = ProcessUtil.Run("sc.exe", $"qc {ServiceName}", 10000);
        if (code != 0) return "?";
        var m = Regex.Match(output, @"START_TYPE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value switch { "AUTO_START" => "自动", "DEMAND_START" => "手动", "DISABLED" => "禁用", var v => v } : "?";
    }

    private static void WaitForServiceStopped(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && ServiceState() is "RUNNING" or "STOP_PENDING") Thread.Sleep(500);
    }

    // ------------------------------------------------------------------ Deskflow service pipe (client + emulation)

    /// <summary>Sends newline-delimited messages to the Deskflow service and waits for each "ok" acknowledgement.</summary>
    public static (bool Ok, string Message) SendDaemon(params string[] messages)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(3000);
            var buffer = new byte[256];
            foreach (var message in messages)
            {
                var bytes = Encoding.UTF8.GetBytes(message + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var read = pipe.ReadAsync(buffer, 0, buffer.Length, cts.Token).GetAwaiter().GetResult();
                var reply = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (reply != "ok") return (false, $"{message.Split('=')[0]} → {(reply.Length == 0 ? "无应答" : reply)}");
            }
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Answers on the Deskflow service's pipe (the service itself is stopped): the layout sync script and the Deskflow
    /// GUI send "command=…" + "start" to reload the core after a layout change; the hub restarts its own core instead.
    /// </summary>
    private void EnsurePipe()
    {
        if (_pipeThread is { IsAlive: true } || ServiceState() == "RUNNING") return;
        _pipeStop = false;
        _pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "DeskflowServicePipe" };
        _pipeThread.Start();
    }

    private void StopPipe()
    {
        _pipeStop = true;
        try
        {
            // unblock WaitForConnection
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            c.Connect(200);
        }
        catch { }
    }

    public void Detach() => StopPipe();

    private void PipeLoop()
    {
        Log.Info("Deskflow: Hub 已接管 deskflow-daemon 管道（布局同步等脚本的重载请求由 Hub 处理）");
        while (!_pipeStop)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.None, 1024, 1024, security);
                server.WaitForConnection();
                if (_pipeStop) return;
                HandlePipeClient(server);
            }
            catch (Exception ex)
            {
                if (_pipeStop) return;
                Log.Warn("Deskflow 管道出错: " + ex.Message);
                Thread.Sleep(2000);
            }
        }
    }

    private void HandlePipeClient(NamedPipeServerStream server)
    {
        var buffer = new byte[4096];
        var pending = new StringBuilder();
        while (server.IsConnected)
        {
            int read;
            try { read = server.Read(buffer, 0, buffer.Length); } catch { return; }
            if (read <= 0) return;
            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            while (pending.ToString().IndexOf('\n') is var idx and >= 0)
            {
                var message = pending.ToString(0, idx).Trim('\r');
                pending.Remove(0, idx + 1);
                var reply = Answer(message);
                if (reply == null) continue;
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                try { server.Write(bytes, 0, bytes.Length); server.Flush(); } catch { return; }
            }
        }
    }

    private string? Answer(string message)
    {
        var key = message.Split('=')[0];
        switch (key)
        {
            case "hello": return "hello";
            case "logPath": return "logPath=" + CoreLogPath;
            case "command":
                _pendingCommand = message.Length > key.Length + 1 ? message[(key.Length + 1)..] : string.Empty;
                return "ok";
            case "start":
                {
                    var role = DeskflowManager.ParseRole(_pendingCommand);
                    Log.Info($"Deskflow: 收到服务管道重载请求（{DeskflowManager.RoleName(role)}），由 Hub 重新启动 core");
                    _reloadRequested(role);
                    return "ok";
                }
            case "stop":
                Log.Info("Deskflow: 收到服务管道停止请求（忽略：角色由 Hub 管理）");
                return "ok";
            default:
                return "ok"; // noop, elevate, logLevel, configFile, clearSettings
        }
    }

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static (int Code, string Output) PowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script));
        return ProcessUtil.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, 30000);
    }
}
