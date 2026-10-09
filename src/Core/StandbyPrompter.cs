namespace MoonlightHub.Core;

/// <summary>A tablet dropped its stream while its virtual display (and the windows on it) are still there.</summary>
public sealed record StandbyPrompt(int InstanceId, int Slot, string InstanceName, DateTime DisconnectedUtc);

/// <summary>
/// Asks before a virtual display is switched off after its Moonlight client disconnected (Extend profiles with stand-by).
/// Sunshine keeps the session and the display when a connection drops (dd_config_revert_on_disconnect is off for these
/// profiles); a few seconds after a disconnect this raises <see cref="Requested"/> if the session is still open (a proper
/// "quit" in Moonlight ends the session and Sunshine switches the display off by itself). The question is withdrawn
/// (<see cref="Resolved"/>) when the tablet reconnects, the session ends or the display goes away.
/// </summary>
public sealed class StandbyPrompter : IDisposable
{
    private readonly HubSettings _settings;
    private readonly SunshineInstanceManager _instances;
    private readonly ProfileEngine _engine;
    private readonly Func<Profile?> _profile;
    private readonly Dictionary<int, StandbyPrompt> _pending = new();
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();

    /// <summary>A prompt should be shown for this instance.</summary>
    public event Action<StandbyPrompt>? Requested;
    /// <summary>The prompt for this instance is no longer needed (reason for the log / UI).</summary>
    public event Action<int, string>? Resolved;

    public StandbyPrompter(HubSettings settings, SunshineInstanceManager instances, ProfileEngine engine, PairingMonitor pairing, Func<Profile?> profile)
    {
        _settings = settings;
        _instances = instances;
        _engine = engine;
        _profile = profile;
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        pairing.ClientConnectionChanged += OnConnectionChanged;
    }

    /// <summary>Sessions whose display is kept although their client is gone (shown in the tray to switch them off later).</summary>
    public List<StandbyPrompt> Pending
    {
        get { lock (_gate) return _pending.Values.ToList(); }
    }

    private void OnConnectionChanged(int instanceId, bool connected)
    {
        if (connected)
        {
            Resolve(instanceId, "平板已重新连接");
            return;
        }
        _ = Task.Run(async () =>
        {
            // a proper "quit" in Moonlight ends the session right after the disconnect: give it a moment
            await Task.Delay(3500).ConfigureAwait(false);
            try { await CheckAsync(instanceId).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("检查断开的串流会话失败: " + ex.Message); }
        });
    }

    private (InstanceSpec Spec, VirtualDisplaySpec Vd)? Target(int instanceId)
    {
        var profile = _profile();
        if (!_settings.ConfirmStandbyOnDisconnect || profile is not { IdleOff: true, Mode: ProfileMode.Extend }) return null;
        var vd = profile.VirtualDisplays.Take(profile.VirtualCount).FirstOrDefault(v => v.InstanceId == instanceId);
        var spec = _settings.Instance(instanceId);
        return vd != null && spec != null ? (spec, vd) : null;
    }

    private static DisplayInfo? ActiveVirtualDisplay(int slot) =>
        DisplayManager.Enumerate().FirstOrDefault(d => d.IsParsec && d.ParsecSlot == slot && d.Active);

    private async Task CheckAsync(int instanceId)
    {
        if (Target(instanceId) is not { } target) return;
        if (_instances.StreamingClientAddresses(target.Spec).Count > 0) return; // already back
        var info = await SunshineApi.GetServerInfoAsync(target.Spec.Port).ConfigureAwait(false);
        if (!info.Reachable || !info.Busy) return; // the session was ended on purpose; Sunshine reverts the display itself
        if (ActiveVirtualDisplay(target.Vd.Slot) == null) return;
        var prompt = new StandbyPrompt(instanceId, target.Vd.Slot, target.Spec.Name, DateTime.UtcNow);
        lock (_gate)
        {
            if (_pending.ContainsKey(instanceId)) return;
            _pending[instanceId] = prompt;
        }
        Log.Info($"实例 {instanceId} 的客户端已断开，但串流会话仍在：保留虚拟屏 #{target.Vd.Slot + 1}，询问是否关闭");
        _timer.Change(2000, 2000);
        try { Requested?.Invoke(prompt); } catch { }
    }

    private void Poll()
    {
        foreach (var prompt in Pending)
        {
            try
            {
                var spec = _settings.Instance(prompt.InstanceId);
                if (spec == null) { Resolve(prompt.InstanceId, "实例已删除"); continue; }
                if (_instances.StreamingClientAddresses(spec).Count > 0) { Resolve(prompt.InstanceId, "平板已重新连接"); continue; }
                var info = SunshineApi.GetServerInfoAsync(spec.Port).GetAwaiter().GetResult();
                if (info.Reachable && !info.Busy) { Resolve(prompt.InstanceId, "串流会话已结束"); continue; }
                if (ActiveVirtualDisplay(prompt.Slot) == null) Resolve(prompt.InstanceId, "虚拟屏已关闭");
            }
            catch (Exception ex)
            {
                Log.Warn("检查待确认的虚拟屏失败: " + ex.Message);
            }
        }
    }

    private void Resolve(int instanceId, string reason)
    {
        bool removed;
        lock (_gate)
        {
            removed = _pending.Remove(instanceId);
            if (_pending.Count == 0) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (!removed) return;
        Log.Info($"实例 {instanceId} 的副屏确认已撤回: {reason}");
        try { Resolved?.Invoke(instanceId, reason); } catch { }
    }

    /// <summary>The user chose to switch the display off: end the session (Sunshine reverts), or detach it directly as a fallback.</summary>
    public async Task<(bool Ok, string Message)> CloseAsync(int instanceId)
    {
        StandbyPrompt? prompt;
        lock (_gate) _pending.TryGetValue(instanceId, out prompt);
        var spec = _settings.Instance(instanceId);
        if (spec == null) return (false, "实例不存在");
        var (ok, msg) = await _instances.Api(spec).CloseAppAsync().ConfigureAwait(false);
        if (ok)
        {
            Log.Info($"用户确认关闭实例 {instanceId} 的副屏：已结束串流会话，由 Sunshine 还原显示配置");
            Resolve(instanceId, "用户确认关闭");
            return (true, "已关闭副屏");
        }
        Log.Warn($"结束实例 {instanceId} 的串流会话失败（{msg}），直接让虚拟屏待机");
        var slot = prompt?.Slot ?? Target(instanceId)?.Vd.Slot;
        var display = slot is { } s ? ActiveVirtualDisplay(s) : null;
        var detached = display != null && _engine.DetachIdleVirtualDisplay(display, "用户确认关闭");
        Resolve(instanceId, "用户确认关闭");
        return detached ? (true, "已关闭副屏（Sunshine 会话未能结束：" + msg + "）") : (false, "关闭副屏失败: " + msg);
    }

    /// <summary>The user keeps the display: stop asking for this disconnect (it stays listed until it resolves).</summary>
    public void Keep(int instanceId) => Log.Info($"用户选择保留实例 {instanceId} 的副屏");

    public void Dispose() => _timer.Dispose();
}
