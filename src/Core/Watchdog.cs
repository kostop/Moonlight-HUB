using Microsoft.Win32;

namespace MoonlightHub.Core;

/// <summary>
/// Replaces the old "Sunshine Connection Guard" and "Moonlight Parsec Display Controller" scripts:
/// keeps the VDD handle alive, re-creates lost virtual displays, restarts dead Sunshine instances and
/// re-applies the active profile after sleep/resume or display topology changes.
/// </summary>
public sealed class Watchdog : IDisposable
{
    private readonly HubSettings _settings;
    private readonly HubState _state;
    private readonly ParsecVdd _vdd;
    private readonly SunshineInstanceManager _instances;
    private readonly ProfileEngine _engine;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool _ticking;
    private DateTime _lastRepairUtc = DateTime.MinValue;
    private DateTime _pendingVerifyUtc = DateTime.MaxValue;
    private int _displayMismatchTicks;
    private readonly Dictionary<int, int> _instanceMissingTicks = new();
    private readonly Dictionary<int, DateTime> _instanceLastRestart = new();

    public bool Enabled { get; set; } = true;
    public DateTime LastTickUtc { get; private set; }
    public string LastSummary { get; private set; } = "尚未运行";
    public event Action? Ticked;

    private readonly bool _subscribed;

    public Watchdog(HubSettings settings, HubState state, ParsecVdd vdd, SunshineInstanceManager instances, ProfileEngine engine, bool subscribeSystemEvents = true)
    {
        _settings = settings;
        _state = state;
        _vdd = vdd;
        _instances = instances;
        _engine = engine;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        if (!subscribeSystemEvents) return;
        try
        {
            _subscribed = true;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            Log.Warn("注册系统事件失败: " + ex.Message);
        }
    }

    public void Start()
    {
        var period = Math.Clamp(_settings.WatchdogSeconds, 2, 120) * 1000;
        _timer.Change(2000, period);
        Log.Info($"守护已启动，每 {period / 1000}s 检查一次");
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void RequestVerify(TimeSpan delay, string reason)
    {
        _pendingVerifyUtc = DateTime.UtcNow + delay;
        Log.Info($"计划在 {delay.TotalSeconds:0}s 后复核布局: {reason}");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            RequestVerify(TimeSpan.FromSeconds(8), "系统从睡眠恢复");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
        {
            RequestVerify(TimeSpan.FromSeconds(5), "会话解锁/重新连接");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_engine.IsBusy) return; // our own change
        try
        {
            var active = DisplayManager.Enumerate().Where(d => d.Active).Select(d => $"{d.GdiName}={d.Width}x{d.Height}@{d.RefreshRate}({d.PositionX},{d.PositionY}){(d.Primary ? "*" : "")}");
            Log.Info("显示设置变化: " + string.Join(" | ", active));
        }
        catch { }
        _manualChangeUtc = DateTime.UtcNow;
        RequestVerify(TimeSpan.FromSeconds(4), "显示设置发生变化");
    }

    private DateTime _manualChangeUtc = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _fpsSwitchUtc = new();
    private DateTime _lastAdapterCheckUtc = DateTime.MinValue;
    private readonly Dictionary<int, int> _captureMismatchTicks = new();
    private DateTime _lastCredentialCheckUtc = DateTime.MinValue;

    /// <summary>Last known PnP status of the Parsec adapter (only refreshed while the driver cannot be opened).</summary>
    public string AdapterStatus { get; private set; } = string.Empty;

    private void Tick()
    {
        if (!Enabled || !_settings.WatchdogEnabled) return;
        lock (_gate)
        {
            if (_ticking) return;
            _ticking = true;
        }
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            Log.Warn("守护检查出错: " + ex.Message);
        }
        finally
        {
            LastTickUtc = DateTime.UtcNow;
            lock (_gate) _ticking = false;
            try { Ticked?.Invoke(); } catch { }
        }
    }

    private void TickCore()
    {
        var notes = new List<string>();

        // 1) keep-alive
        if (_state.ExpectedVirtualDisplays > 0)
        {
            if (!_vdd.IsOpen)
            {
                if (_vdd.Open())
                {
                    notes.Add("重新打开 VDD");
                }
                else
                {
                    notes.Add("VDD 不可用");
                    if ((DateTime.UtcNow - _lastAdapterCheckUtc) > TimeSpan.FromSeconds(60))
                    {
                        _lastAdapterCheckUtc = DateTime.UtcNow;
                        AdapterStatus = ParsecVdd.QueryAdapterStatus();
                        if (!AdapterStatus.StartsWith("Started", StringComparison.OrdinalIgnoreCase) && !AdapterStatus.StartsWith("已启动", StringComparison.Ordinal))
                        {
                            Log.Error($"Parsec Virtual Display Adapter 设备状态异常: {AdapterStatus} —— 请在“虚拟屏”页点击「重启适配器」（需要管理员权限）");
                        }
                    }
                }
            }
            else if (!_vdd.IsPinging)
            {
                _vdd.StartPing();
                notes.Add("重启 keep-alive");
            }
            else if (_vdd.ConsecutivePingFailures >= 20)
            {
                Log.Warn("VDD keep-alive 连续失败，重新打开设备");
                _vdd.Close();
                _vdd.Open();
            }
        }

        _instances.PollAsync().GetAwaiter().GetResult();

        if (_engine.IsBusy)
        {
            LastSummary = "正在应用方案…";
            return;
        }

        var profile = _settings.Profile(_state.ActiveProfileId);
        if (profile == null || !_state.LastApplySucceeded && _state.LastAppliedUtc == default)
        {
            LastSummary = "没有已应用的方案";
            return;
        }

        var health = _engine.CheckHealth();
        var needRepair = false;

        // 2) virtual displays vanished (driver reset, sleep, other tools)
        if (!health.VirtualDisplaysOk)
        {
            _displayMismatchTicks++;
            notes.Add($"虚拟屏 {health.ParsecActive}/{health.ParsecExpected}");
            if (_displayMismatchTicks >= 2) needRepair = true;
        }
        else
        {
            _displayMismatchTicks = 0;
        }

        // 3) drifted capture targets
        if (health.DriftedInstances.Count > 0)
        {
            notes.Add("抓取目标失效: " + string.Join(",", health.DriftedInstances));
            needRepair = true;
        }

        // 4) dead instances
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            var missing = health.MissingInstances.Contains(id) || rt.State == InstanceState.Unhealthy;
            _instanceMissingTicks[id] = missing ? _instanceMissingTicks.GetValueOrDefault(id) + 1 : 0;
            if (_instanceMissingTicks[id] >= 3)
            {
                var last = _instanceLastRestart.GetValueOrDefault(id, DateTime.MinValue);
                if ((DateTime.UtcNow - last) > TimeSpan.FromSeconds(45))
                {
                    _instanceLastRestart[id] = DateTime.UtcNow;
                    _instanceMissingTicks[id] = 0;
                    Log.Warn($"守护: 实例 {id} 不健康 ({rt.LastError})，重启");
                    notes.Add($"重启实例 {id}");
                    _ = Task.Run(async () =>
                    {
                        if (_state.InstanceOutputIds.TryGetValue(id, out var outputId)) _instances.EnsureConfig(spec, outputId, out _);
                        await _instances.RestartAsync(spec).ConfigureAwait(false);
                    });
                }
            }
        }

        // 4a) a running Sunshine session keeps the GDI name it resolved at start; when the virtual display was
        //     re-created under a new name, the session silently falls back to the primary display → restart it.
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            if (rt.State != InstanceState.Streaming) continue;
            if (!_state.InstanceOutputIds.TryGetValue(id, out var outputId) || string.IsNullOrEmpty(outputId)) continue;
            var target = health.Displays.FirstOrDefault(d => d.Active && d.SunshineDeviceId.Equals(outputId, StringComparison.OrdinalIgnoreCase));
            var captured = _instances.LastCaptureResolution(spec);
            if (target == null || captured == null) { _captureMismatchTicks[id] = 0; continue; }
            var mismatch = captured.Value.Width != target.Width || captured.Value.Height != target.Height;
            _captureMismatchTicks[id] = mismatch ? _captureMismatchTicks.GetValueOrDefault(id) + 1 : 0;
            if (_captureMismatchTicks[id] >= 2 && (DateTime.UtcNow - _instanceLastRestart.GetValueOrDefault(id, DateTime.MinValue)) > TimeSpan.FromSeconds(60))
            {
                _instanceLastRestart[id] = DateTime.UtcNow;
                _captureMismatchTicks[id] = 0;
                Log.Warn($"守护: 实例 {id} 的会话仍在抓取 {captured.Value.Width}x{captured.Value.Height}，而目标虚拟屏是 {target.GdiName} {target.Width}x{target.Height}，重启实例让客户端重新连接到副屏");
                notes.Add($"实例 {id} 抓取目标错位，重启");
                _ = Task.Run(() => _instances.RestartAsync(spec));
                continue;
            }
        }

        // 4b) follow the stream mode (resolution + fps) the connected client asked for
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            if (rt.State != InstanceState.Streaming) continue;

            var mode = _instances.LastRequestedMode(spec);
            var fps = mode?.Fps ?? _instances.LastRequestedFps(spec);
            if (mode == null && fps == null) continue;
            var dirty = false;
            if (mode != null && (!_state.LastClientMode.TryGetValue(id, out var knownMode) || knownMode != mode))
            {
                _state.LastClientMode[id] = mode;
                dirty = true;
            }
            if (fps != null && (!_state.LastClientFps.TryGetValue(id, out var known) || known != fps))
            {
                _state.LastClientFps[id] = fps.Value;
                dirty = true;
            }
            if (dirty) _state.Save();

            if (!profile.MatchClientFps && !profile.AutoMatchClientResolution) continue;
            if ((DateTime.UtcNow - _fpsSwitchUtc.GetValueOrDefault(id, DateTime.MinValue)) < TimeSpan.FromSeconds(30)) continue;
            var switched = mode != null ? _engine.ApplyClientMode(id, mode) : _engine.ApplyClientFps(id, fps!.Value);
            if (switched != null)
            {
                _fpsSwitchUtc[id] = DateTime.UtcNow;
                notes.Add($"实例 {id} 客户端 {(mode?.ToString() ?? fps + "fps")} → 虚拟屏{(switched == true ? "已匹配" : "无对应模式")}");
            }
        }

        // 4c) credentials for pairing: generate them while the instance is idle
        if (_settings.AutoManageCredentials && (DateTime.UtcNow - _lastCredentialCheckUtc) > TimeSpan.FromSeconds(60))
        {
            _lastCredentialCheckUtc = DateTime.UtcNow;
            foreach (var id in _state.ExpectedInstances)
            {
                var spec = _settings.Instance(id);
                if (spec == null || !spec.Enabled || _settings.GetPassword(id) != null) continue;
                var rt = _instances.Get(spec);
                if (rt.State == InstanceState.Streaming || _engine.IsBusy) continue;
                notes.Add($"为实例 {id} 生成 Web 凭据");
                _ = Task.Run(() => _instances.EnsureCredentialsAsync(spec, allowRestart: true));
                break; // one at a time
            }
        }

        // 4d) stand-by mode: switch idle virtual displays off; restart instances whose config changed once they are idle
        if (health.IdleOff)
        {
            foreach (var spec in profile.VirtualDisplays.Take(profile.VirtualCount))
            {
                if (spec.InstanceId <= 0) continue;
                var inst = _settings.Instance(spec.InstanceId);
                if (inst == null) continue;
                var rt = _instances.Get(inst);
                var d = health.Displays.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == spec.Slot);
                if (d == null) continue;
                if (rt.State == InstanceState.Streaming)
                {
                    _lastStreamingUtc[spec.InstanceId] = DateTime.UtcNow;
                    _activeSinceUtc.Remove(d.GdiName);
                    continue;
                }
                if (!d.Active)
                {
                    _activeSinceUtc.Remove(d.GdiName);
                    if (rt.ConfigDirty && rt.IsAlive && !_engine.IsBusy && (DateTime.UtcNow - _instanceLastRestart.GetValueOrDefault(spec.InstanceId, DateTime.MinValue)) > TimeSpan.FromSeconds(45))
                    {
                        _instanceLastRestart[spec.InstanceId] = DateTime.UtcNow;
                        Log.Info($"实例 {spec.InstanceId} 空闲，重启以应用新的配置");
                        notes.Add($"重启实例 {spec.InstanceId}（配置已更新）");
                        _ = Task.Run(() => _instances.RestartAsync(inst));
                    }
                    continue;
                }
                // active while nobody streams: give Sunshine's own revert (3 s) and a fresh session a chance first
                if (!_activeSinceUtc.ContainsKey(d.GdiName)) _activeSinceUtc[d.GdiName] = DateTime.UtcNow;
                var activeFor = DateTime.UtcNow - _activeSinceUtc[d.GdiName];
                var sinceStream = DateTime.UtcNow - _lastStreamingUtc.GetValueOrDefault(spec.InstanceId, DateTime.MinValue);
                if (activeFor > TimeSpan.FromSeconds(20) && sinceStream > TimeSpan.FromSeconds(20) && !_engine.IsBusy)
                {
                    if (_engine.DetachIdleVirtualDisplay(d, "无客户端连接")) notes.Add($"虚拟屏 #{spec.Slot + 1} 待机");
                    _activeSinceUtc.Remove(d.GdiName);
                }
            }
        }

        // 5) scheduled verification (resume / display change)
        if (DateTime.UtcNow >= _pendingVerifyUtc)
        {
            _pendingVerifyUtc = DateTime.MaxValue;
            if (_manualChangeUtc != DateTime.MinValue && health.VirtualDisplaysOk)
            {
                _manualChangeUtc = DateTime.MinValue;
                if (_engine.RememberVirtualPositions(health.Displays)) notes.Add("已记住手动摆放的位置");
            }
            if (!health.VirtualDisplaysOk || health.DriftedInstances.Count > 0 || !LayoutMatches(profile, health.Displays, health.IdleOff))
            {
                needRepair = true;
                notes.Add("复核发现布局偏差");
            }
            else
            {
                notes.Add("复核通过");
            }
        }

        if (needRepair && (DateTime.UtcNow - _lastRepairUtc) > TimeSpan.FromSeconds(40))
        {
            _lastRepairUtc = DateTime.UtcNow;
            Log.Warn("守护: 重新应用方案「" + profile.Name + "」 (" + string.Join("; ", notes) + ")");
            _ = _engine.ApplyAsync(profile);
        }

        LastSummary = notes.Count == 0
            ? $"正常 · 虚拟屏 {health.ParsecActive}/{health.ParsecExpected} · 实例 {string.Join(",", _state.ExpectedInstances)}"
            : string.Join("; ", notes);
    }

    private bool LayoutMatches(Profile profile, List<DisplayInfo> displays, bool idleOff = false)
    {
        if (profile.Mode == ProfileMode.MainOnly) return !displays.Any(d => d.IsParsec && d.Active);
        var specs = profile.VirtualDisplays.OrderBy(v => v.Slot).Take(profile.VirtualCount).ToList();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var d = displays.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == i);
            if (d == null) return false;
            if (idleOff)
            {
                // stand-by: an inactive display is fine; while a session runs Sunshine owns mode/refresh rate
                var inst = spec.InstanceId > 0 ? _settings.Instance(spec.InstanceId) : null;
                var streaming = inst != null && _instances.Get(inst).State == InstanceState.Streaming;
                if (!d.Active || !streaming) continue;
                continue;
            }
            if (!d.Active) return false;
            // resolution: the profile value or the client-derived one
            var okResolution = d.Width == spec.Width && d.Height == spec.Height;
            if (!okResolution && profile.AutoMatchClientResolution && spec.InstanceId > 0 && _state.LastClientMode.TryGetValue(spec.InstanceId, out var cm))
            {
                var (hw, hh) = _engine.HostResolutionFor(spec.InstanceId, cm);
                okResolution = d.Width == hw && d.Height == hh;
            }
            if (!okResolution) return false;
            // refresh rate: accept the profile value, the compose override, or the rate the client asked for
            var accepted = new HashSet<int> { spec.RefreshRate };
            if (profile.ComposeRefreshRate > 0) accepted.Add(Math.Max(profile.ComposeRefreshRate, spec.RefreshRate));
            if (profile.MatchClientFps && spec.InstanceId > 0 && _state.LastClientFps.TryGetValue(spec.InstanceId, out var cf)) accepted.Add(cf);
            if (profile.MatchClientFps && spec.InstanceId > 0 && _state.LastClientMode.TryGetValue(spec.InstanceId, out var cm2)) accepted.Add(cm2.Fps);
            if (!accepted.Any(a => Math.Abs(d.RefreshRate - a) <= 1)) return false;
        }
        if (profile.Mode == ProfileMode.VirtualOnly && !idleOff && displays.Any(d => d.Active && !d.IsParsec)) return false;
        return true;
    }

    private readonly Dictionary<int, DateTime> _lastStreamingUtc = new();
    private readonly Dictionary<string, DateTime> _activeSinceUtc = new();

    public void Dispose()
    {
        _timer.Dispose();
        if (!_subscribed) return;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { }
    }
}
