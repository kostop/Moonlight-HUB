using System.Diagnostics;
using System.IO;
using System.Text;

namespace MoonlightHub.Core;

public enum InstanceState { Disabled, Stopped, Starting, Running, Streaming, Unhealthy }

public sealed class InstanceRuntime
{
    public InstanceSpec Spec { get; init; } = null!;
    public string ConfigDir { get; init; } = string.Empty;
    public Process? Process { get; set; }
    public int Pid { get; set; }
    public InstanceState State { get; set; } = InstanceState.Stopped;
    public DateTime LastStartUtc { get; set; }
    public int RestartCount { get; set; }
    public string DesiredOutputId { get; set; } = string.Empty;
    /// <summary>Display-device options the config was last written with.</summary>
    public DdOptions DesiredDd { get; set; } = DdOptions.Disabled;
    /// <summary>The config file changed while the process was running; a restart is needed to pick it up.</summary>
    public bool ConfigDirty { get; set; }
    public ServerInfo? Info { get; set; }
    public HashSet<int> ListeningPorts { get; set; } = new();
    public string LastError { get; set; } = string.Empty;
    public int UnhealthyTicks { get; set; }
    public DateTime LastRestartUtc { get; set; }

    public string ConfigPath => Path.Combine(ConfigDir, "sunshine.conf");
    public string LogPath => Path.Combine(ConfigDir, "sunshine.log");
    public string AppsPath => Path.Combine(ConfigDir, "apps.json");
    public string StatePath => Path.Combine(ConfigDir, "sunshine_state.json");

    public string StateText => State switch
    {
        InstanceState.Disabled => "未启用",
        InstanceState.Stopped => "已停止",
        InstanceState.Starting => "启动中",
        InstanceState.Running => "运行中",
        InstanceState.Streaming => "串流中",
        InstanceState.Unhealthy => "异常",
        _ => State.ToString()
    };

    public bool IsAlive => State is InstanceState.Running or InstanceState.Streaming or InstanceState.Starting;
}

/// <summary>
/// Runs one sunshine.exe per virtual display. Every instance gets its own base port, config, state/credentials, log and
/// apps file; the executable and assets are shared. Instance 1 keeps using the original config directory so existing
/// client pairings survive.
/// </summary>
public sealed class SunshineInstanceManager
{
    private static readonly string[] ManagedKeys =
    {
        "port", "sunshine_name", "output_name", "dd_configuration_option", "dd_resolution_option", "dd_manual_resolution",
        "dd_refresh_rate_option", "dd_manual_refresh_rate", "dd_config_revert_on_disconnect", "dd_hdr_option", "dd_mode_remapping",
        "dd_wa_hdr_toggle", "dd_wa_hdr_toggle_delay", "dd_config_revert_delay", "file_state", "credentials_file", "log_path", "file_apps", "pkey", "cert", "system_tray", "stream_audio",
        "minimum_fps_target", "upnp"
    };

    private const string ManagedMarker = "# ---- managed by Moonlight Hub";

    private readonly HubSettings _settings;
    private readonly Dictionary<int, InstanceRuntime> _runtimes = new();
    private readonly object _gate = new();

    public SunshineInstanceManager(HubSettings settings)
    {
        _settings = settings;
    }

    public event Action? Changed;

    public InstanceRuntime Get(InstanceSpec spec)
    {
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(spec.Id, out var rt))
            {
                rt = new InstanceRuntime
                {
                    Spec = spec,
                    ConfigDir = spec.Id == 1 ? _settings.Instance1ConfigDir : Path.Combine(_settings.InstancesRoot, spec.Id.ToString()),
                    State = spec.Enabled ? InstanceState.Stopped : InstanceState.Disabled
                };
                _runtimes[spec.Id] = rt;
            }
            return rt;
        }
    }

    public IEnumerable<InstanceRuntime> All => _settings.Instances.OrderBy(i => i.Id).Select(Get).ToList();

    public SunshineApi Api(InstanceSpec spec) => new(spec.Port, _settings.GetUser(spec.Id), _settings.GetPassword(spec.Id));

    // ------------------------------------------------------------------ configuration
    /// <summary>Writes the per-instance sunshine.conf with the last known display-device options.</summary>
    public bool EnsureConfig(InstanceSpec spec, string outputId, out string message)
    {
        return EnsureConfig(spec, outputId, Get(spec).DesiredDd, out message);
    }

    /// <summary>Writes the per-instance sunshine.conf. Returns true when the file content changed.</summary>
    public bool EnsureConfig(InstanceSpec spec, string outputId, DdOptions dd, out string message)
    {
        var rt = Get(spec);
        Directory.CreateDirectory(rt.ConfigDir);
        rt.DesiredOutputId = outputId;
        rt.DesiredDd = dd;

        // apps.json
        if (!File.Exists(rt.AppsPath))
        {
            var candidates = new[]
            {
                Path.Combine(_settings.Instance1ConfigDir, "apps.json"),
                Path.Combine(_settings.SunshineDir, "assets", "apps.json"),
                Path.Combine(_settings.SunshineDir, "config", "apps.json"),
            };
            var src = candidates.FirstOrDefault(File.Exists);
            if (src != null) File.Copy(src, rt.AppsPath);
            else File.WriteAllText(rt.AppsPath, "{\n  \"env\": {},\n  \"apps\": [\n    { \"name\": \"Desktop\", \"image-path\": \"desktop.png\" }\n  ]\n}\n");
        }

        var template = spec.Id == 1 ? ReadConfigLines(rt.ConfigPath) : ReadConfigLines(Path.Combine(_settings.Instance1ConfigDir, "sunshine.conf"));
        var lines = new List<string>();
        foreach (var line in template)
        {
            var key = KeyOf(line);
            if (key == null) { lines.Add(line); continue; }
            if (ManagedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            lines.Add(line);
        }

        var managed = new List<(string Key, string Value)>
        {
            ("port", spec.Port.ToString()),
            ("sunshine_name", spec.Name),
            ("output_name", outputId),
            ("system_tray", spec.SystemTray ? "enabled" : "disabled"),
            ("stream_audio", spec.Audio ? "enabled" : "disabled"),
            ("minimum_fps_target", spec.MinimumFpsTarget.ToString()),
            ("upnp", "disabled"),
        };
        if (dd.Enabled && !string.IsNullOrEmpty(outputId))
        {
            // Sunshine activates the (idle, switched-off) virtual display right before the session and reverts afterwards.
            managed.Add(("dd_configuration_option", dd.Configuration));
            managed.Add(("dd_resolution_option", "manual"));
            managed.Add(("dd_manual_resolution", $"{dd.Width}x{dd.Height}"));
            if (dd.ManualHz is > 0)
            {
                managed.Add(("dd_refresh_rate_option", "manual"));
                managed.Add(("dd_manual_refresh_rate", dd.ManualHz.Value.ToString()));
            }
            else
            {
                managed.Add(("dd_refresh_rate_option", "automatic"));
            }
            managed.Add(("dd_config_revert_on_disconnect", dd.RevertOnDisconnect ? "enabled" : "disabled"));
            managed.Add(("dd_config_revert_delay", dd.RevertDelayMs.ToString()));
        }
        else
        {
            managed.Add(("dd_configuration_option", "disabled"));
        }
        if (spec.Id != 1)
        {
            var credDir = Path.Combine(rt.ConfigDir, "credentials");
            Directory.CreateDirectory(credDir);
            managed.Add(("file_state", rt.StatePath));
            managed.Add(("log_path", rt.LogPath));
            managed.Add(("file_apps", rt.AppsPath));
            managed.Add(("pkey", Path.Combine(credDir, "cakey.pem")));
            managed.Add(("cert", Path.Combine(credDir, "cacert.pem")));
        }

        if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add(string.Empty);
        lines.Add(ManagedMarker + " (do not edit these keys by hand) ----");
        foreach (var (key, value) in managed)
        {
            if (string.IsNullOrEmpty(value) && key == "output_name") continue; // empty = primary display
            lines.Add($"{key} = {value}");
        }

        var newText = string.Join("\n", lines).TrimEnd() + "\n";
        var oldText = File.Exists(rt.ConfigPath) ? File.ReadAllText(rt.ConfigPath) : string.Empty;
        if (Normalize(oldText) == Normalize(newText))
        {
            message = "配置无变化";
            return false;
        }
        if (File.Exists(rt.ConfigPath))
        {
            try { File.Copy(rt.ConfigPath, rt.ConfigPath + ".hub-backup", true); } catch { }
        }
        File.WriteAllText(rt.ConfigPath, newText, new UTF8Encoding(false));
        message = $"已写入 {rt.ConfigPath}";
        if (rt.IsAlive) rt.ConfigDirty = true;
        Log.Info($"实例 {spec.Id} 配置更新: port={spec.Port} output_name={(string.IsNullOrEmpty(outputId) ? "(主屏)" : outputId)} dd={dd}");
        return true;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static List<string> ReadConfigLines(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
        // everything from the marker on is the block we regenerate; never carry it over as "user" lines
        var marker = lines.FindIndex(l => l.StartsWith(ManagedMarker, StringComparison.Ordinal));
        if (marker >= 0) lines = lines.Take(marker).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string? KeyOf(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) return null;
        var idx = t.IndexOf('=');
        return idx <= 0 ? null : t[..idx].Trim();
    }

    public Dictionary<string, string> ReadConfig(InstanceSpec spec)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadConfigLines(Get(spec).ConfigPath))
        {
            var key = KeyOf(line);
            if (key == null) continue;
            var idx = line.IndexOf('=');
            dict[key] = line[(idx + 1)..].Trim();
        }
        return dict;
    }

    // ------------------------------------------------------------------ process control
    public bool Adopt(InstanceSpec spec)
    {
        var rt = Get(spec);
        var pid = TcpTable.GetListeningPid(spec.Port);
        if (pid is null or 0)
        {
            return false;
        }
        try
        {
            var p = Process.GetProcessById(pid.Value);
            if (p.HasExited) return false;
            rt.Process = p;
            rt.Pid = pid.Value;
            if (rt.State is InstanceState.Stopped or InstanceState.Disabled or InstanceState.Starting) rt.State = InstanceState.Running;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartAsync(InstanceSpec spec, CancellationToken ct = default)
    {
        var rt = Get(spec);
        if (!File.Exists(_settings.SunshineExe))
        {
            rt.LastError = "找不到 sunshine.exe: " + _settings.SunshineExe;
            rt.State = InstanceState.Unhealthy;
            Log.Error(rt.LastError);
            return false;
        }
        if (Adopt(spec))
        {
            Log.Info($"实例 {spec.Id} 已在运行 (pid {rt.Pid})，直接接管");
            Changed?.Invoke();
            return true;
        }

        // Another process (old scheduled task etc.) may be holding the ports without being adoptable.
        foreach (var port in spec.RequiredTcpPorts)
        {
            var owner = TcpTable.GetListeningPid(port);
            if (owner is > 0)
            {
                rt.LastError = $"端口 {port} 被进程 {owner} 占用";
                Log.Warn($"实例 {spec.Id}: {rt.LastError}");
            }
        }

        rt.State = InstanceState.Starting;
        rt.LastStartUtc = DateTime.UtcNow;
        Changed?.Invoke();
        try
        {
            var psi = new ProcessStartInfo(_settings.SunshineExe, $"\"{rt.ConfigPath}\"")
            {
                WorkingDirectory = _settings.SunshineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("Process.Start 返回 null");
            rt.Process = p;
            rt.Pid = p.Id;
            try { p.PriorityClass = _settings.HighProcessPriority ? ProcessPriorityClass.High : ProcessPriorityClass.Normal; } catch { }
            Log.Info($"实例 {spec.Id} ({spec.Name}) 已启动 pid={p.Id} port={spec.Port}");
        }
        catch (Exception ex)
        {
            rt.State = InstanceState.Unhealthy;
            rt.LastError = ex.Message;
            Log.Error($"实例 {spec.Id} 启动失败", ex);
            Changed?.Invoke();
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (rt.Process!.HasExited)
            {
                rt.State = InstanceState.Unhealthy;
                rt.LastError = $"进程退出，退出码 {rt.Process.ExitCode}";
                Log.Error($"实例 {spec.Id} 启动后立即退出: {rt.LastError}");
                Changed?.Invoke();
                return false;
            }
            var ports = TcpTable.GetListeningPorts(rt.Pid);
            if (spec.RequiredTcpPorts.All(ports.Contains))
            {
                rt.ListeningPorts = ports;
                rt.State = InstanceState.Running;
                rt.UnhealthyTicks = 0;
                rt.ConfigDirty = false;
                Log.Info($"实例 {spec.Id} 端口就绪: {string.Join(",", spec.RequiredTcpPorts)}");
                Changed?.Invoke();
                return true;
            }
        }
        rt.State = InstanceState.Unhealthy;
        rt.LastError = "30 秒内端口未就绪";
        Log.Warn($"实例 {spec.Id}: {rt.LastError}");
        Changed?.Invoke();
        return false;
    }

    public async Task StopAsync(InstanceSpec spec, bool graceful = true)
    {
        var rt = Get(spec);
        var pid = rt.Pid;
        if (pid == 0 || rt.Process == null || SafeHasExited(rt.Process))
        {
            var listening = TcpTable.GetListeningPid(spec.Port);
            pid = listening ?? 0;
        }
        if (pid != 0)
        {
            Log.Info($"停止实例 {spec.Id} (pid {pid}, {(graceful ? "Ctrl+C" : "kill")})");
            await Task.Run(() => graceful ? ProcessUtil.StopGracefully(pid, TimeSpan.FromSeconds(8)) : KillHard(pid)).ConfigureAwait(false);
        }
        rt.Process?.Dispose();
        rt.Process = null;
        rt.Pid = 0;
        rt.ListeningPorts.Clear();
        rt.Info = null;
        rt.State = spec.Enabled ? InstanceState.Stopped : InstanceState.Disabled;
        Changed?.Invoke();
    }

    private static bool KillHard(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(true);
            return p.WaitForExit(5000);
        }
        catch { return true; }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    public async Task<bool> RestartAsync(InstanceSpec spec, CancellationToken ct = default)
    {
        await StopAsync(spec).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
        var rt = Get(spec);
        rt.RestartCount++;
        rt.LastRestartUtc = DateTime.UtcNow;
        return await StartAsync(spec, ct).ConfigureAwait(false);
    }

    /// <summary>Stops every sunshine.exe from the configured install (used when taking over from the old scheduled tasks).</summary>
    public async Task StopAllSunshineProcessesAsync()
    {
        foreach (var p in ProcessUtil.FindByPath(_settings.SunshineExe))
        {
            try
            {
                Log.Info($"结束外部 Sunshine 进程 pid={p.Id}");
                await Task.Run(() => ProcessUtil.StopGracefully(p.Id, TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            }
            finally
            {
                p.Dispose();
            }
        }
        foreach (var rt in All)
        {
            rt.Process = null;
            rt.Pid = 0;
            rt.State = rt.Spec.Enabled ? InstanceState.Stopped : InstanceState.Disabled;
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ polling
    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var rt in All)
        {
            var spec = rt.Spec;
            if (!spec.Enabled)
            {
                rt.State = InstanceState.Disabled;
                continue;
            }
            var pid = TcpTable.GetListeningPid(spec.Port);
            if (pid is null or 0)
            {
                if (rt.State == InstanceState.Starting && (DateTime.UtcNow - rt.LastStartUtc) < TimeSpan.FromSeconds(30)) continue;
                if (rt.Process != null && !SafeHasExited(rt.Process))
                {
                    rt.State = InstanceState.Unhealthy;
                    rt.LastError = "进程存在但端口未监听";
                }
                else
                {
                    rt.State = rt.State == InstanceState.Unhealthy ? InstanceState.Unhealthy : InstanceState.Stopped;
                    rt.Pid = 0;
                }
                rt.Info = null;
                continue;
            }
            rt.Pid = pid.Value;
            rt.ListeningPorts = TcpTable.GetListeningPorts(pid.Value);
            var info = await SunshineApi.GetServerInfoAsync(spec.Port, ct).ConfigureAwait(false);
            rt.Info = info;
            if (!info.Reachable)
            {
                rt.UnhealthyTicks++;
                rt.State = rt.UnhealthyTicks >= 3 ? InstanceState.Unhealthy : InstanceState.Running;
                rt.LastError = rt.UnhealthyTicks >= 3 ? "/serverinfo 无响应" : rt.LastError;
            }
            else
            {
                rt.UnhealthyTicks = 0;
                rt.State = info.Busy ? InstanceState.Streaming : InstanceState.Running;
            }
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ credentials
    /// <summary>Sets the web UI username/password of an instance with "sunshine.exe &lt;conf&gt; --creds user pass" and stores them (DPAPI).</summary>
    public async Task<(bool Ok, string Message)> SetCredentialsAsync(InstanceSpec spec, string user, string password)
    {
        var rt = Get(spec);
        if (!File.Exists(rt.ConfigPath))
        {
            EnsureConfig(spec, rt.DesiredOutputId, out _);
        }
        var wasRunning = rt.IsAlive || Adopt(spec);
        if (wasRunning)
        {
            await StopAsync(spec).ConfigureAwait(false);
        }
        var (code, output) = await Task.Run(() => ProcessUtil.Run(_settings.SunshineExe, $"\"{rt.ConfigPath}\" --creds \"{user}\" \"{password}\"", 30000, _settings.SunshineDir)).ConfigureAwait(false);
        if (code != 0)
        {
            Log.Error($"设置实例 {spec.Id} 凭据失败 (exit {code}): {output}");
            if (wasRunning) await StartAsync(spec).ConfigureAwait(false);
            return (false, $"sunshine --creds 退出码 {code}: {output}");
        }
        _settings.SetCredential(spec.Id, user, password);
        _settings.Save();
        Log.Info($"实例 {spec.Id} 的 Web 凭据已更新 (用户 {user})");
        if (wasRunning) await StartAsync(spec).ConfigureAwait(false);
        return (true, "凭据已设置并保存");
    }

    /// <summary>Extra instances get generated credentials automatically so the hub can drive pairing.</summary>
    public async Task EnsureGeneratedCredentialsAsync(InstanceSpec spec)
    {
        if (spec.Id == 1) return;
        await EnsureCredentialsAsync(spec, allowRestart: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the hub owns working web credentials for the instance (needed for /api/pin). Missing credentials are
    /// generated with "sunshine --creds"; when the instance is running this restarts it, so callers pass
    /// <paramref name="allowRestart"/> = false while a client is streaming.
    /// </summary>
    public async Task<bool> EnsureCredentialsAsync(InstanceSpec spec, bool allowRestart)
    {
        if (!_settings.AutoManageCredentials) return _settings.GetPassword(spec.Id) != null;
        if (_settings.GetPassword(spec.Id) != null) return true;
        var rt = Get(spec);
        var running = rt.IsAlive || Adopt(spec);
        if (running && (!allowRestart || rt.State == InstanceState.Streaming))
        {
            Log.Info($"实例 {spec.Id} 还没有 Web 凭据，等实例空闲后再自动生成");
            return false;
        }
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var (ok, message) = await SetCredentialsAsync(spec, "hub", password).ConfigureAwait(false);
        if (!ok) Log.Warn($"实例 {spec.Id} 自动生成凭据失败: {message}");
        else Log.Info($"实例 {spec.Id} 已自动生成 Web 凭据（用户 hub），可在“客户端”页查看");
        return ok;
    }

    /// <summary>Remote addresses of clients holding an RTSP connection (= actively streaming) to this instance.</summary>
    public List<string> StreamingClientAddresses(InstanceSpec spec)
    {
        try { return TcpTable.RemoteAddressesFor(spec.RtspPort); }
        catch { return new List<string>(); }
    }

    /// <summary>Latest "Client requested mode [WxHxF]" line in the log (fallback when the live monitor missed it).</summary>
    public ClientMode? LastRequestedMode(InstanceSpec spec)
    {
        var rt = Get(spec);
        try
        {
            if (!File.Exists(rt.LogPath)) return null;
            var text = ReadTail(rt.LogPath, 96 * 1024);
            const string marker = "Client requested mode [";
            var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var end = text.IndexOf(']', idx);
            if (end < 0) return null;
            var parts = text[(idx + marker.Length)..end].Split('x');
            if (parts.Length != 3) return null;
            return int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && int.TryParse(parts[2], out var f) && w > 0 && h > 0 ? new ClientMode(w, h, f) : null;
        }
        catch
        {
            return null;
        }
    }

    public string TailLog(InstanceSpec spec, int lines = 200)
    {
        var rt = Get(spec);
        try
        {
            if (!File.Exists(rt.LogPath)) return string.Empty;
            var text = ReadTail(rt.LogPath, Math.Max(64 * 1024, lines * 512));
            var all = text.Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return "无法读取日志: " + ex.Message;
        }
    }

    /// <summary>Reads at most the last <paramref name="maxBytes"/> of a (possibly large, shared) log file.</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Resolution of the display the most recent capture initialised on ("Desktop resolution [2000x1200]").</summary>
    public (int Width, int Height)? LastCaptureResolution(InstanceSpec spec)
    {
        var rt = Get(spec);
        try
        {
            if (!File.Exists(rt.LogPath)) return null;
            var text = ReadTail(rt.LogPath, 96 * 1024);
            const string marker = "Desktop resolution [";
            var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var end = text.IndexOf(']', idx);
            if (end < 0) return null;
            var parts = text[(idx + marker.Length)..end].Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Frame rate the most recent client session asked for ("Requested frame rate [90fps]"), if any.</summary>
    public int? LastRequestedFps(InstanceSpec spec)
    {
        var rt = Get(spec);
        try
        {
            if (!File.Exists(rt.LogPath)) return null;
            var text = ReadTail(rt.LogPath, 96 * 1024);
            const string marker = "Requested frame rate [";
            var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var end = text.IndexOf("fps]", idx, StringComparison.Ordinal);
            if (end < 0) return null;
            var num = text[(idx + marker.Length)..end];
            return int.TryParse(num, out var fps) && fps is > 0 and <= 1000 ? fps : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Looks at the latest "Currently available display devices" block written after the instance started.</summary>
    public bool? VerifyCaptureTarget(InstanceSpec spec, string outputId)
    {
        if (string.IsNullOrEmpty(outputId)) return null;
        var text = TailLog(spec, 800);
        var idx = text.LastIndexOf("Currently available display devices", StringComparison.Ordinal);
        if (idx < 0) return null;
        var block = text[idx..];
        var pos = block.IndexOf(outputId, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return false;
        var tail = block[pos..];
        var dn = tail.IndexOf("\"display_name\"", StringComparison.Ordinal);
        if (dn < 0) return null;
        var lineEnd = tail.IndexOf('\n', dn);
        var line = lineEnd > 0 ? tail[dn..lineEnd] : tail[dn..];
        return line.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase);
    }
}
