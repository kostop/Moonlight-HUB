using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>
/// Linux: Deskflow is the Flatpak "org.deskflow.deskflow". The hub starts deskflow-core itself (as the Deskflow GUI did
/// in its desktop process mode), keeps the shared layout service (deskflow-layout-web) running, and replaces the GUI's
/// autostart and the old connection watchdog.
/// </summary>
internal sealed class DeskflowLinuxHost : IDeskflowHost
{
    public const string AppId = "org.deskflow.deskflow";
    public const string WatchdogService = "deskflow-connection-watchdog.service";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppDir => Path.Combine(Home, ".var", "app", AppId);
    private static string AutostartDir => Path.Combine(Home, ".config", "autostart");
    private static string GuiAutostartFile => Path.Combine(AutostartDir, AppId + ".desktop");
    private const string BackupSuffix = ".bak-moonlight-hub";

    private bool? _installed;
    private DateTime _lastLayoutStartUtc = DateTime.MinValue;
    private DateTime _lastAgentCheckUtc = DateTime.MinValue;
    private bool _agentsPrepared;

    /// <summary>Side-channel agents (systemd user units) with their control ports.</summary>
    private static readonly (string Unit, int Port, bool FileAgent)[] SideChannelAgents =
    {
        ("deskflow-clipboard-agent-linux.service", SideChannelHealth.ClipboardControlPort, false),
        ("deskflow-file-agent-linux.service", SideChannelHealth.FileControlPort, true),
    };

    public string ConfigPath => Path.Combine(AppDir, "config", "Deskflow", "Deskflow.conf");
    public string CoreLogPath => Path.Combine(AppDir, "data", "deskflow-core.log");
    public string InstallLocation => "Flatpak " + AppId;
    public string LayoutHost(string peerHost) => "127.0.0.1"; // the shared layout service runs on this computer

    public bool Installed
    {
        get
        {
            if (_installed is { } known) return known;
            try
            {
                var (code, _) = ProcessUtil.Run("flatpak", "info " + AppId, 10000);
                _installed = code == 0;
            }
            catch { _installed = false; }
            return _installed.Value;
        }
    }

    private static string FlatpakRun(string command, string args) =>
        $"run --branch=master --arch=x86_64 --command={command} {AppId} {args}".TrimEnd();

    // ------------------------------------------------------------------ core processes

    public List<CoreProcess> FindCores()
    {
        var list = new List<CoreProcess>();
        foreach (var dir in SafeProcDirs())
        {
            var comm = ReadProc(dir, "comm")?.Trim();
            if (comm != "deskflow-core") continue;
            var cmd = (ReadProc(dir, "cmdline") ?? string.Empty).Replace('\0', ' ').Trim();
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            list.Add(new CoreProcess(pid, DeskflowManager.ParseRole(cmd), false, "/app/bin/deskflow-core", cmd));
        }
        return list;
    }

    private static IEnumerable<string> SafeProcDirs()
    {
        try { return Directory.GetDirectories("/proc").Where(d => Path.GetFileName(d).All(char.IsDigit)); }
        catch { return Array.Empty<string>(); }
    }

    private static string? ReadProc(string dir, string file)
    {
        try { return File.ReadAllText(Path.Combine(dir, file)); } catch { return null; }
    }

    public int Start(DeskflowRole role, DeskflowSettings settings)
    {
        if (!Installed) throw new InvalidOperationException("没有安装 Flatpak 版 Deskflow（" + AppId + "）");
        EnsureLogConfig();
        var mode = role == DeskflowRole.Server ? "server" : "client";
        var pid = Spawn("flatpak", FlatpakRun("deskflow-core", mode));
        Log.Info($"Deskflow: 已启动 deskflow-core {mode} (flatpak pid {pid})");
        return pid;
    }

    private static int Spawn(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, WorkingDirectory = Home };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + file);
        return p.Id;
    }

    /// <summary>The core writes its log inside the Flatpak's own data folder (the only place it may write).</summary>
    private void EnsureLogConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CoreLogPath)!);
            var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;
            var updated = DeskflowManager.IniSet(text, "log", "toFile", "true");
            updated = DeskflowManager.IniSet(updated, "log", "file", CoreLogPath);
            if (updated != text) File.WriteAllText(ConfigPath, updated, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("写入 Deskflow 日志设置失败: " + ex.Message);
        }
    }

    /// <summary>Asks each core to quit through its IPC socket (clean shutdown), then kills what is left.</summary>
    public void Stop(IReadOnlyCollection<CoreProcess> cores)
    {
        foreach (var core in cores)
        {
            try
            {
                var socketPath = $"/proc/{core.Pid}/root/tmp/deskflow-core";
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.ReceiveTimeout = 2000;
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                socket.Send(Encoding.UTF8.GetBytes("stop\n"));
                var buffer = new byte[64];
                try { socket.Receive(buffer); } catch { }
            }
            catch { }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline && cores.Any(c => Directory.Exists($"/proc/{c.Pid}"))) Thread.Sleep(200);
        foreach (var core in cores.Where(c => Directory.Exists($"/proc/{c.Pid}")))
        {
            try { using var p = Process.GetProcessById(core.Pid); p.Kill(); } catch { }
        }
    }

    // ------------------------------------------------------------------ connections (/proc/net/tcp)

    private sealed record TcpEntry(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int State);

    private static IEnumerable<TcpEntry> ReadTcp()
    {
        foreach (var (file, v6) in new[] { ("/proc/net/tcp", false), ("/proc/net/tcp6", true) })
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                var local = ParseEndpoint(parts[1], v6);
                var remote = ParseEndpoint(parts[2], v6);
                if (local == null || remote == null) continue;
                yield return new TcpEntry(local.Value.Address, local.Value.Port, remote.Value.Address, remote.Value.Port, int.Parse(parts[3], NumberStyles.HexNumber));
            }
        }
    }

    private static (IPAddress Address, int Port)? ParseEndpoint(string text, bool v6)
    {
        var idx = text.IndexOf(':');
        if (idx < 0) return null;
        var hex = text[..idx];
        var port = int.Parse(text[(idx + 1)..], NumberStyles.HexNumber);
        var bytes = Convert.FromHexString(hex);
        if (!v6)
        {
            Array.Reverse(bytes); // little-endian 32-bit word
            return (new IPAddress(bytes), port);
        }
        // IPv6: four little-endian 32-bit words
        for (var i = 0; i < 16; i += 4) Array.Reverse(bytes, i, 4);
        var address = new IPAddress(bytes);
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, port);
    }

    public (bool Listening, List<CoreConnection> Connections) Connections(int port, DeskflowRole role, IReadOnlyCollection<int> corePids)
    {
        var listening = false;
        var list = new List<CoreConnection>();
        try
        {
            foreach (var e in ReadTcp())
            {
                if (e.State == 0x0A && e.LocalPort == port && role == DeskflowRole.Server) listening = true;
                if (e.State != 0x01 || IPAddress.IsLoopback(e.Remote)) continue;
                var mine = role == DeskflowRole.Server ? e.LocalPort == port : e.RemotePort == port;
                if (mine) list.Add(new CoreConnection($"{e.LocalPort}:{e.Remote}:{e.RemotePort}", e.Remote.ToString()));
            }
        }
        catch { }
        return (listening, list);
    }

    private static bool PortListening(int port)
    {
        try { return ReadTcp().Any(e => e.State == 0x0A && e.LocalPort == port); } catch { return false; }
    }

    public bool? CheckFilter(IReadOnlyCollection<CoreProcess> cores) => null;
    public bool F24RulePresent() => false;
    public bool MonitorRunning() => false;
    public bool TouchBridgeRunning() => false;
    public void PrepareForCoreRestart(DeskflowSettings settings) { }

    // ------------------------------------------------------------------ upkeep

    public void Tick(DeskflowSettings settings, DeskflowStatus status)
    {
        SuperviseSideChannelAgents();

        // the shared screen layout service used to live as long as the Deskflow GUI; now the hub keeps it up
        if (!settings.LegacyTakenOver || PortListening(DeskflowPeer.LayoutPort)) return;
        if ((DateTime.UtcNow - _lastLayoutStartUtc) < TimeSpan.FromSeconds(30)) return;
        _lastLayoutStartUtc = DateTime.UtcNow;
        try
        {
            var pid = Spawn("flatpak", FlatpakRun("deskflow-layout-web", $"--host 0.0.0.0 --port {DeskflowPeer.LayoutPort} --display-host 127.0.0.1"));
            Log.Info($"Deskflow: 已启动共享布局服务 deskflow-layout-web (pid {pid})");
        }
        catch (Exception ex)
        {
            Log.Warn("启动共享布局服务失败: " + ex.Message);
        }
    }

    public void Detach() { }

    // ------------------------------------------------------------------ clipboard / file side channel

    /// <summary>
    /// The clipboard and file agents talk to the X display of the desktop session. Their units used to pin DISPLAY=:0,
    /// so after a re-login on another display (e.g. :1 after a GNOME Shell crash) they kept running but could no longer
    /// read or write the clipboard. The hub runs inside the current session: it lets the units inherit the session's
    /// DISPLAY, restarts an agent that is on another display, and restarts agents whose health check fails three
    /// times in a row (what the old connection watchdog did).
    /// </summary>
    private void SuperviseSideChannelAgents()
    {
        if ((DateTime.UtcNow - _lastAgentCheckUtc) < TimeSpan.FromSeconds(10)) return;
        _lastAgentCheckUtc = DateTime.UtcNow;
        if (!_agentsPrepared)
        {
            _agentsPrepared = true;
            PrepareAgentUnits();
        }
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        foreach (var (unit, port, fileAgent) in SideChannelAgents)
        {
            var pid = MainPid(unit);
            if (pid <= 0) continue; // not installed or not running: systemd's own Restart= takes care of it
            var agentDisplay = ReadEnvironment(pid, "DISPLAY");
            if (!string.IsNullOrEmpty(display) && agentDisplay != display)
            {
                Log.Warn($"Deskflow 旁路: {unit} 仍在显示 {agentDisplay ?? "(无)"}，当前桌面是 {display}，重启以恢复剪贴板");
                RestartAgent(unit);
                continue;
            }
            var (healthy, detail) = SideChannelHealth.Check(port, fileAgent);
            if (SideChannelHealth.NeedsRestart(unit, healthy) && ProcessUtil.Run("systemctl", "--user is-active --quiet deskflow-clipboard-hub.service", 5000).ExitCode == 0)
            {
                Log.Warn($"Deskflow 旁路: {unit} 连续 3 次健康检查失败（{detail}），重启");
                RestartAgent(unit);
            }
        }
    }

    /// <summary>Drop-ins that clear the pinned DISPLAY/XAUTHORITY so the agents inherit the session's values.</summary>
    private static void PrepareAgentUnits()
    {
        try
        {
            var unitDir = Path.Combine(Home, ".config", "systemd", "user");
            var changed = false;
            foreach (var (unit, _, _) in SideChannelAgents)
            {
                if (!File.Exists(Path.Combine(unitDir, unit))) continue;
                var dropIn = Path.Combine(unitDir, unit + ".d", "moonlight-hub-session.conf");
                if (File.Exists(dropIn)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dropIn)!);
                File.WriteAllText(dropIn, string.Join('\n',
                    "# Moonlight Hub: follow the current desktop session (DISPLAY changes after a re-login, e.g. :0 -> :1)",
                    "[Service]",
                    "Environment=",
                    "Environment=XDG_RUNTIME_DIR=/run/user/%U",
                    string.Empty));
                changed = true;
            }
            if (changed) ProcessUtil.Run("systemctl", "--user daemon-reload", 20000);
            // the units take DISPLAY / XAUTHORITY from the user manager: make sure it has this session's values
            ProcessUtil.Run("systemctl", "--user import-environment DISPLAY XAUTHORITY", 10000);
        }
        catch (Exception ex)
        {
            Log.Warn("准备剪贴板代理的 systemd 配置失败: " + ex.Message);
        }
    }

    private static void RestartAgent(string unit)
    {
        ProcessUtil.Run("systemctl", "--user import-environment DISPLAY XAUTHORITY", 10000);
        var (code, output) = ProcessUtil.Run("systemctl", $"--user restart {unit}", 20000);
        Log.Info($"Deskflow 旁路: 重启 {unit} {(code == 0 ? "ok" : output.Trim())}");
    }

    private static int MainPid(string unit)
    {
        var (code, output) = ProcessUtil.Run("systemctl", $"--user show -p MainPID --value {unit}", 5000);
        return code == 0 && int.TryParse(output.Trim(), out var pid) ? pid : 0;
    }

    private static string? ReadEnvironment(int pid, string name)
    {
        var text = ReadProc($"/proc/{pid}", "environ");
        if (text == null) return null;
        foreach (var entry in text.Split('\0'))
        {
            if (entry.StartsWith(name + "=", StringComparison.Ordinal)) return entry[(name.Length + 1)..];
        }
        return null;
    }

    // ------------------------------------------------------------------ legacy autostart pieces

    public List<string> TakeOverLegacy(DeskflowSettings settings)
    {
        var notes = new List<string>();
        try
        {
            if (File.Exists(GuiAutostartFile))
            {
                var text = File.ReadAllText(GuiAutostartFile);
                if (!File.Exists(GuiAutostartFile + BackupSuffix)) File.Copy(GuiAutostartFile, GuiAutostartFile + BackupSuffix);
                var updated = SetDesktopKey(SetDesktopKey(text, "Hidden", "true"), "X-GNOME-Autostart-enabled", "false");
                if (updated != text)
                {
                    File.WriteAllText(GuiAutostartFile, updated);
                    notes.Add("Deskflow 界面开机自启: 已关闭");
                }
            }
        }
        catch (Exception ex) { notes.Add("关闭 Deskflow 界面自启失败: " + ex.Message); }

        var (code, output) = ProcessUtil.Run("systemctl", $"--user disable --now {WatchdogService}", 20000);
        notes.Add($"{WatchdogService}: {(code == 0 ? "已停用" : output.Trim())}");

        // the GUI owns a core of its own in desktop mode: close it, the hub starts its own core right after
        foreach (var dir in SafeProcDirs())
        {
            if (ReadProc(dir, "comm")?.Trim() != "deskflow" || !int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try { using var p = Process.GetProcessById(pid); p.Kill(); notes.Add($"已关闭 Deskflow 界面 (pid {pid})"); } catch { }
        }

        settings.LegacyTakenOver = true;
        Log.Info("Deskflow: 已接管开机自启与后台组件 — " + string.Join("；", notes));
        return notes;
    }

    public List<string> RestoreLegacy(DeskflowSettings settings)
    {
        var notes = new List<string>();
        try
        {
            if (File.Exists(GuiAutostartFile + BackupSuffix))
            {
                File.Copy(GuiAutostartFile + BackupSuffix, GuiAutostartFile, overwrite: true);
                File.Delete(GuiAutostartFile + BackupSuffix);
                notes.Add("Deskflow 界面开机自启: 已恢复");
            }
        }
        catch (Exception ex) { notes.Add("恢复 Deskflow 界面自启失败: " + ex.Message); }
        var (code, output) = ProcessUtil.Run("systemctl", $"--user enable --now {WatchdogService}", 20000);
        notes.Add($"{WatchdogService}: {(code == 0 ? "已启用" : output.Trim())}");
        var gui = ProcessUtil.Run("systemctl", $"--user start app-{AppId}@autostart.service", 20000);
        notes.Add("Deskflow 界面: " + (gui.ExitCode == 0 ? "已启动" : gui.Output.Trim()));
        settings.LegacyTakenOver = false;
        return notes;
    }

    public string LegacyStatus()
    {
        var hidden = File.Exists(GuiAutostartFile) && Regex.IsMatch(File.ReadAllText(GuiAutostartFile), @"(?m)^Hidden\s*=\s*true");
        var (_, watchdog) = ProcessUtil.Run("systemctl", $"--user is-active {WatchdogService}", 10000);
        return $"Deskflow 界面开机自启: {(hidden ? "已关闭" : File.Exists(GuiAutostartFile) ? "开启" : "无")}\n{WatchdogService}: {watchdog.Trim()}";
    }

    /// <summary>Sets a key of the [Desktop Entry] group of a .desktop file.</summary>
    private static string SetDesktopKey(string text, string key, string value)
    {
        var pattern = new Regex($@"(?m)^{Regex.Escape(key)}\s*=.*$");
        if (pattern.IsMatch(text)) return pattern.Replace(text, $"{key}={value}");
        var idx = text.IndexOf("[Desktop Entry]", StringComparison.Ordinal);
        if (idx < 0) return text.TrimEnd() + $"\n{key}={value}\n";
        var lineEnd = text.IndexOf('\n', idx);
        return lineEnd < 0 ? text + $"\n{key}={value}\n" : text.Insert(lineEnd + 1, $"{key}={value}\n");
    }
}

/// <summary>Autostart of the hub itself on Linux (XDG autostart entry).</summary>
public static class LinuxAutostart
{
    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "moonlight-hub.desktop");

    public static bool IsEnabled()
    {
        try { return File.Exists(File_) && !Regex.IsMatch(File.ReadAllText(File_), @"(?m)^Hidden\s*=\s*true"); }
        catch { return false; }
    }

    public static (bool Ok, string Message) Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(File_)) File.Delete(File_);
                return (true, "已关闭 Moonlight Hub 开机自启");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            var exe = Paths.ExePath;
            File.WriteAllText(File_, $"""
                [Desktop Entry]
                Type=Application
                Name=Moonlight Hub
                Comment=Deskflow 键鼠共享（服务端 / 客户端）与共享屏幕布局
                Exec="{exe}" --minimized
                Icon=input-keyboard
                Terminal=false
                X-GNOME-Autostart-enabled=true
                X-GNOME-Autostart-Delay=3

                """);
            return (true, "已设置 Moonlight Hub 开机自启: " + File_);
        }
        catch (Exception ex)
        {
            return (false, "设置开机自启失败: " + ex.Message);
        }
    }
}
