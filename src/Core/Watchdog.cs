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
        _lastDisplayChangeUtc = DateTime.UtcNow;
        RequestVerify(TimeSpan.FromSeconds(12), "显示设置发生变化");
    }

    private DateTime _manualChangeUtc = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _fpsSwitchUtc = new();
    private DateTime _lastAdapterCheckUtc = DateTime.MinValue;
    private readonly Dictionary<int, int> _captureMismatchTicks = new();
    private DateTime _lastCredentialCheckUtc = DateTime.MinValue;
    private DateTime _lastModeSyncUtc = DateTime.MinValue;

    /// <summary>Last known PnP status of the Parsec adapter (only refreshed while the driver cannot be opened).</summary>
    public string AdapterStatus { get; private set; } = string.Empty;

    private DateTime _tickStartedUtc = DateTime.MinValue;

    private void Tick()
    {
        if (!Enabled || !_settings.WatchdogEnabled) return;
        lock (_gate)
        {
            if (_ticking)
            {
                // a tick stuck for minutes (hung driver call, blocked process spawn) must not silence the watchdog forever
                if ((DateTime.UtcNow - _tickStartedUtc) > TimeSpan.FromMinutes(3))
                {
                    Log.Error("守护上一轮检查超过 3 分钟未结束，放弃等待并继续新的检查");
                }
                else
                {
                    return;
                }
            }
            _ticking = true;
            _tickStartedUtc = DateTime.UtcNow;
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

        // 1) driver / keep-alive
        if (_state.ExpectedVirtualDisplays > 0)
        {
            var driverNote = EnsureDriverAlive();
            if (driverNote != null) notes.Add(driverNote);
        }
        CheckKeepAliveStalls(notes);

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
        // Windows re-enumerates everything during a monitor hot-plug (DisplayPort link retrain, KVM switch, remote
        // desktop tools): for a few seconds the virtual display may be missing although the driver still has it.
        // Sending ADD/REMOVE into that window is exactly what used to hang the driver, so repairs wait it out.
        var settling = (DateTime.UtcNow - _lastDisplayChangeUtc) < TimeSpan.FromSeconds(12);
        // …unless the keep-alive just stalled for about a second: then the driver unplugged the displays itself and
        // there is no hot-plug to wait out, so the repair starts right away
        var urgentRepair = false;

        // 2) virtual displays vanished (driver reset, sleep, other tools)
        if (!health.VirtualDisplaysOk)
        {
            _displayMismatchTicks++;
            urgentRepair = (DateTime.UtcNow - _keepAliveStallUtc) < TimeSpan.FromSeconds(60);
            notes.Add($"虚拟屏 {health.ParsecActive}/{health.ParsecExpected}"
                + (urgentRepair ? "（keep-alive 停顿后被驱动移除，立即修复）" : settling ? "（显示器正在重新枚举，等待）" : string.Empty));
            if (urgentRepair || _displayMismatchTicks >= 3 && !settling) needRepair = true;
        }
        else
        {
            _displayMismatchTicks = 0;
        }
        if (!_vdd.IsOpen) needRepair = false; // nothing to repair with until the driver is back

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
        //     After an urgent repair (display unplugged by the driver mid-stream) that is certain: restart at once.
        var restartAfterUrgentRepair = _restartStreamingAfterRepair;
        _restartStreamingAfterRepair = false;
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            if (rt.State != InstanceState.Streaming) continue;
            if (!_state.InstanceOutputIds.TryGetValue(id, out var outputId) || string.IsNullOrEmpty(outputId)) continue;
            var target = health.Displays.FirstOrDefault(d => d.Active && d.SunshineDeviceId.Equals(outputId, StringComparison.OrdinalIgnoreCase));
            var captured = _instances.LastCaptureResolution(spec);
            if (restartAfterUrgentRepair && target != null
                && (captured == null || captured.Value.Width != target.Width || captured.Value.Height != target.Height)
                && (DateTime.UtcNow - _instanceLastRestart.GetValueOrDefault(id, DateTime.MinValue)) > TimeSpan.FromSeconds(60))
            {
                _instanceLastRestart[id] = DateTime.UtcNow;
                _captureMismatchTicks[id] = 0;
                Log.Warn($"守护: 虚拟屏在串流中被驱动移除后已重建，实例 {id} 的会话仍在抓取{(captured == null ? "未知目标" : $" {captured.Value.Width}x{captured.Value.Height}")}，立即重启实例让客户端重新连接到 {target.GdiName}");
                notes.Add($"实例 {id} 抓取目标错位，重启");
                _ = Task.Run(() => _instances.RestartAsync(spec));
                continue;
            }
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

        // 4c2) config changed while a session was running (e.g. a refresh-rate pick): restart once the instance is idle
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            if (rt.State == InstanceState.Streaming) { _lastStreamingUtc[id] = DateTime.UtcNow; continue; }
            if (!rt.ConfigDirty || !rt.IsAlive || _engine.IsBusy) continue;
            if (_instances.StreamingClientAddresses(spec).Count > 0) continue;
            if ((DateTime.UtcNow - _lastStreamingUtc.GetValueOrDefault(id, DateTime.MinValue)) < TimeSpan.FromSeconds(10)) continue; // let Sunshine finish its own revert first
            if ((DateTime.UtcNow - _instanceLastRestart.GetValueOrDefault(id, DateTime.MinValue)) < TimeSpan.FromSeconds(45)) continue;
            _instanceLastRestart[id] = DateTime.UtcNow;
            Log.Info($"实例 {id} 空闲，重启以应用串流期间更新的配置");
            notes.Add($"重启实例 {id}（配置已更新）");
            _ = Task.Run(() => _instances.RestartAsync(spec));
            break; // one per tick
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
                var sinceDetachFailure = DateTime.UtcNow - _detachFailedUtc.GetValueOrDefault(d.GdiName, DateTime.MinValue);
                if (activeFor > TimeSpan.FromSeconds(20) && sinceStream > TimeSpan.FromSeconds(20) && sinceDetachFailure > TimeSpan.FromMinutes(10) && !settling && !_engine.IsBusy)
                {
                    if (_engine.DetachIdleVirtualDisplay(d, "无客户端连接"))
                    {
                        notes.Add($"虚拟屏 #{spec.Slot + 1} 待机");
                        _detachFailedUtc.Remove(d.GdiName);
                    }
                    else
                    {
                        // e.g. DISP_CHANGE_BADPARAM while Windows is mid-reconfiguration: do not hammer the API every 20 s
                        _detachFailedUtc[d.GdiName] = DateTime.UtcNow;
                        Log.Warn($"虚拟屏 #{spec.Slot + 1} 无法进入待机，10 分钟后再试");
                    }
                    _activeSinceUtc.Remove(d.GdiName);
                }
            }
        }

        // 4e) driver mode table: re-plug virtual displays whose registered modes changed, once nobody streams them
        if (profile.VirtualCount > 0 && (DateTime.UtcNow - _lastModeSyncUtc) > TimeSpan.FromSeconds(30) && !_engine.IsBusy)
        {
            _lastModeSyncUtc = DateTime.UtcNow;
            foreach (var spec in profile.VirtualDisplays.Take(profile.VirtualCount))
            {
                var d = health.Displays.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == spec.Slot);
                if (d == null) continue;
                var pending = _engine.IsReplugPending(spec.Slot) || (d.IsReady && _engine.NeedsReplug(profile, spec, d));
                if (!pending) continue;
                if (_engine.IsSpecStreaming(spec))
                {
                    if (!_engine.IsReplugPending(spec.Slot)) _engine.RequestReplug(spec.Slot, "驱动模式表与注册表不一致");
                    continue;
                }
                notes.Add($"重新插拔虚拟屏 #{spec.Slot + 1}（刷新驱动模式表）");
                _ = _engine.ReplugAsync(spec.Slot, "驱动模式表已更新且当前空闲");
                break; // one at a time
            }
        }

        // 5) scheduled verification (resume / display change)
        if (DateTime.UtcNow >= _pendingVerifyUtc)
        {
            if (settling)
            {
                _pendingVerifyUtc = DateTime.UtcNow.AddSeconds(6); // still changing: look again later
                return;
            }
            _pendingVerifyUtc = DateTime.MaxValue;
            if (_manualChangeUtc != DateTime.MinValue && health.VirtualDisplaysOk)
            {
                _manualChangeUtc = DateTime.MinValue;
                if (_engine.RememberLayout(health.Displays)) notes.Add("已记住手动摆放的位置");
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

        if (needRepair && (!settling || urgentRepair) && (DateTime.UtcNow - _lastRepairUtc) > RepairInterval())
        {
            _lastRepairUtc = DateTime.UtcNow;
            if (urgentRepair) _keepAliveStallUtc = DateTime.MinValue; // one urgent repair per stall
            Log.Warn("守护: 重新应用方案「" + profile.Name + "」 (" + string.Join("; ", notes) + ")" + (_failedRepairs > 0 ? $"，连续失败 {_failedRepairs} 次" : string.Empty));
            _ = _engine.ApplyAsync(profile).ContinueWith(t =>
            {
                var ok = t.Status == TaskStatus.RanToCompletion && t.Result.Success;
                _failedRepairs = ok ? 0 : Math.Min(_failedRepairs + 1, 8);
                if (!ok && _failedRepairs == 3) Log.Error("守护: 连续 3 次修复失败，降低重试频率（最长每 10 分钟一次）: " + (t.Exception?.Message ?? t.Result?.Summary));
                if (ok && urgentRepair) _restartStreamingAfterRepair = true; // consumed by the next tick (4a)
            }, TaskScheduler.Default);
        }

        LastSummary = notes.Count == 0
            ? $"正常 · 虚拟屏 {health.ParsecActive}/{health.ParsecExpected} · 实例 {string.Join(",", _state.ExpectedInstances)}"
            : string.Join("; ", notes);
    }

    private DateTime _keepAliveStallUtc = DateTime.MinValue;
    private volatile bool _restartStreamingAfterRepair;
    private int _pendingGapMaxMs;
    private int _pendingLongGaps;
    private DateTime _lastGapLogUtc = DateTime.MinValue;

    /// <summary>
    /// The driver unplugs every virtual display after about a second without a ping (2026-09-30 15:06:40: the tablet
    /// display vanished while a disk-heavy image restore ran). Gaps of 300 ms and more are logged with their likely
    /// cause (at most once a minute, a potentially fatal one at once); one of 900 ms and more makes the next repair of
    /// missing displays urgent.
    /// </summary>
    private void CheckKeepAliveStalls(List<string> notes)
    {
        if (!_vdd.IsOpen) return;
        var ka = _vdd.TakeKeepAliveReport();
        if (ka.MaxGapMs >= 300)
        {
            _pendingLongGaps += Math.Max(1, ka.LongGaps);
            if (ka.MaxGapMs > _pendingGapMaxMs)
            {
                _pendingGapMaxMs = ka.MaxGapMs;
                _pendingGapCause = !ka.Native ? "托管线程停顿（GC 暂停或内存页换入）"
                    : ka.MaxIoMs * 2 >= ka.MaxGapMs ? $"驱动响应慢（单次 UPDATE 耗时 {ka.MaxIoMs} ms）"
                    : "整个系统停顿，原生 ping 线程没有被调度";
            }
        }
        var lethal = ka.MaxGapMs >= 900;
        if (_pendingGapMaxMs == 0 || !lethal && (DateTime.UtcNow - _lastGapLogUtc) < TimeSpan.FromMinutes(1)) return;
        Log.Warn($"VDD keep-alive 停顿: 最长 {_pendingGapMaxMs} ms（≥300 ms 共 {_pendingLongGaps} 次）：{_pendingGapCause}"
            + (lethal ? "；超过约 1 s 驱动会移除所有虚拟屏" : string.Empty));
        _lastGapLogUtc = DateTime.UtcNow;
        _pendingGapMaxMs = 0;
        _pendingLongGaps = 0;
        if (!lethal) return;
        _keepAliveStallUtc = DateTime.UtcNow;
        notes.Add($"keep-alive 停顿 {ka.MaxGapMs} ms");
    }

    private string _pendingGapCause = string.Empty;

    private int _failedRepairs;
    private int _driverRecoveries;
    private DateTime _lastDriverRecoveryUtc = DateTime.MinValue;
    private DateTime _lastDriverInstallUtc = DateTime.MinValue;
    private DateTime _lastDisplayChangeUtc = DateTime.MinValue;

    /// <summary>Repair interval grows with consecutive failures: 40 s, 80 s, 160 s … capped at 10 minutes.</summary>
    private TimeSpan RepairInterval() => TimeSpan.FromSeconds(Math.Min(600, 40 * Math.Pow(2, _failedRepairs)));

    /// <summary>Recovery interval for the adapter: 20 s, 40 s, 80 s … capped at 10 minutes, reset by a successful open.</summary>
    private TimeSpan DriverRecoveryInterval() => TimeSpan.FromSeconds(Math.Min(600, 20 * Math.Pow(2, Math.Min(_driverRecoveries, 6))));

    /// <summary>Last adapter state seen by the watchdog (for the UI).</summary>
    public ParsecVdd.DeviceState? AdapterState { get; private set; }

    /// <summary>
    /// Keeps the driver handle usable: reopens it, restarts the adapter when the user-mode driver host died (problem
    /// code 43 after a display hot-plug), installs the bundled driver when it is missing. Everything that needs
    /// administrator rights only runs when the hub is elevated; otherwise it logs once and leaves the button to the user.
    /// </summary>
    private string? EnsureDriverAlive()
    {
        if (_vdd.IsOpen && _vdd.IsPinging && !_vdd.Faulted && _vdd.ConsecutivePingFailures < 3)
        {
            if (_driverRecoveries > 0 && (DateTime.UtcNow - _lastDriverRecoveryUtc) > TimeSpan.FromMinutes(5)) _driverRecoveries = 0;
            return null;
        }
        if (_vdd.IsOpen && !_vdd.Faulted && _vdd.ConsecutivePingFailures < 3)
        {
            _vdd.StartPing();
            return "重启 keep-alive";
        }
        if (_vdd.IsOpen)
        {
            Log.Warn($"VDD 停止响应（{(_vdd.Faulted ? "IOCTL 超时" : $"keep-alive 连续失败 {_vdd.ConsecutivePingFailures} 次")}），关闭句柄后检查设备状态");
            _vdd.Close();
        }

        var state = ParsecVdd.QueryDeviceState();
        AdapterState = state;
        AdapterStatus = state.Text;
        if (state.Health == ParsecVdd.DeviceHealth.Ok && _vdd.Open())
        {
            _driverRecoveries = 0;
            return "重新打开 VDD";
        }

        // the interface is gone or the device reports a problem: repair (rate limited, escalating back-off)
        if ((DateTime.UtcNow - _lastDriverRecoveryUtc) < DriverRecoveryInterval()) return "VDD 不可用: " + state.Text;
        _lastDriverRecoveryUtc = DateTime.UtcNow;
        _driverRecoveries++;

        if (state.Health == ParsecVdd.DeviceHealth.Missing)
        {
            if (!_settings.AutoInstallDriver || !ParsecVdd.HasBundledSetup || (DateTime.UtcNow - _lastDriverInstallUtc) < TimeSpan.FromMinutes(15))
            {
                if (_driverRecoveries == 1) Log.Error("未找到 Parsec Virtual Display Adapter：驱动未安装。请在“虚拟屏”页点击「安装驱动」");
                return "驱动未安装";
            }
            if (!ProcessUtil.IsElevated())
            {
                if (_driverRecoveries == 1) Log.Error("驱动未安装，且当前不是管理员权限，无法自动安装。请在“虚拟屏”页点击「安装驱动」");
                return "驱动未安装";
            }
            _lastDriverInstallUtc = DateTime.UtcNow;
            Log.Warn("驱动未安装，自动安装随附的 Parsec 虚拟显示驱动…");
            var (iok, imsg) = ParsecVdd.InstallDriver();
            Log.Info("自动安装驱动: " + imsg);
            if (iok && _vdd.Open()) { _driverRecoveries = 0; return "驱动已安装"; }
            return "驱动安装: " + imsg;
        }

        if (!_settings.AutoRestartAdapter)
        {
            if (_driverRecoveries == 1) Log.Error($"Parsec Virtual Display Adapter 状态: {state.Text} —— 自动重启已关闭，请在“虚拟屏”页点击「重启适配器」");
            return "VDD 不可用: " + state.Text;
        }
        if (!ProcessUtil.IsElevated())
        {
            if (_driverRecoveries == 1) Log.Error($"Parsec Virtual Display Adapter 状态: {state.Text} —— 当前不是管理员权限，无法自动重启。请在“虚拟屏”页点击「重启适配器」，或在设置里开启“以管理员权限运行”");
            return "VDD 不可用: " + state.Text;
        }
        Log.Warn($"Parsec Virtual Display Adapter 状态: {state.Text}，自动重启适配器（第 {_driverRecoveries} 次）");
        var (ok, msg) = ParsecVdd.RestartAdapterAndWait(TimeSpan.FromSeconds(25));
        Log.Info("自动重启适配器: " + msg);
        if (ok && _vdd.Open())
        {
            _driverRecoveries = 0;
            _displayMismatchTicks = 3; // the displays are certainly gone: let the repair run on the next tick
            _lastRepairUtc = DateTime.MinValue;
            return "适配器已恢复";
        }
        return "适配器重启失败: " + msg;
    }

    /// <summary>Restarts / installs the driver right away (start-up and the UI buttons use this; it honours the same rules as the tick).</summary>
    public string RecoverDriverNow(string reason)
    {
        lock (_gate)
        {
            _lastDriverRecoveryUtc = DateTime.MinValue;
            _lastDriverInstallUtc = DateTime.MinValue;
        }
        Log.Info("检查 Parsec 驱动状态: " + reason);
        return EnsureDriverAlive() ?? "驱动正常";
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
            var accepted = new HashSet<int> { spec.RefreshRate, _engine.TargetHz(profile, spec, d) };
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
    private readonly Dictionary<string, DateTime> _detachFailedUtc = new();

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
