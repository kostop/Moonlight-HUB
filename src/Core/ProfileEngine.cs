using MoonlightHub.Native;

namespace MoonlightHub.Core;

public sealed class ApplyResult
{
    public bool Success { get; set; } = true;
    public List<string> Steps { get; } = new();
    public string Summary { get; set; } = string.Empty;
    public List<DisplayInfo> Displays { get; set; } = new();
}

/// <summary>
/// Turns a <see cref="Profile"/> into reality: virtual displays (Parsec VDD), Windows display layout, and one
/// Sunshine instance per streamed display. Only one apply/repair runs at a time.
/// </summary>
public sealed class ProfileEngine
{
    private readonly HubSettings _settings;
    private readonly HubState _state;
    private readonly ParsecVdd _vdd;
    private readonly SunshineInstanceManager _instances;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public ProfileEngine(HubSettings settings, HubState state, ParsecVdd vdd, SunshineInstanceManager instances)
    {
        _settings = settings;
        _state = state;
        _vdd = vdd;
        _instances = instances;
    }

    public bool IsBusy => _busy.CurrentCount == 0;
    public string CurrentStep { get; private set; } = string.Empty;
    public event Action<string>? Progress;
    public event Action? Applied;

    /// <summary>True when the instance's Sunshine config rotates the stream for portrait clients (windows_client_pre_rotation != 0).</summary>
    private bool PreRotationEnabled(int instanceId)
    {
        var spec = _settings.Instance(instanceId);
        if (spec == null) return false;
        try
        {
            var cfg = _instances.ReadConfig(spec);
            return cfg.TryGetValue("windows_client_pre_rotation", out var v) && v.Trim() != "0";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Host-side resolution for a client request: portrait requests become landscape when Sunshine pre-rotates.</summary>
    public (int Width, int Height) HostResolutionFor(int instanceId, ClientMode mode)
    {
        if (mode.Height > mode.Width && PreRotationEnabled(instanceId)) return (mode.Height, mode.Width);
        return (mode.Width, mode.Height);
    }

    /// <summary>Resolution for a virtual display: the client's requested size (if that mode exists on the display) else the profile value.</summary>
    private (int Width, int Height) ResolveResolution(Profile profile, VirtualDisplaySpec spec, string? gdiName)
    {
        if (profile.AutoMatchClientResolution && spec.InstanceId > 0 && _state.LastClientMode.TryGetValue(spec.InstanceId, out var mode) && mode.Width > 0 && mode.Height > 0)
        {
            var (w, h) = HostResolutionFor(spec.InstanceId, mode);
            if (string.IsNullOrWhiteSpace(gdiName) || DisplayManager.GetModes(gdiName, w, h).Any(m => m.Orientation == 0)) return (w, h);
        }
        return (spec.Width, spec.Height);
    }

    /// <summary>
    /// Target refresh rate for a virtual display. The profile value is the floor, "follow client fps" raises it to the
    /// client's frame rate and the compose override raises it further; the result is snapped to a rate the driver has
    /// (registered in the registry or exposed by Windows right now): the exact rate, else the smallest multiple of it,
    /// else the next higher rate, else the highest one available.
    /// </summary>
    private int ResolveHz(Profile profile, VirtualDisplaySpec spec, string? gdiName, int width, int height)
        => SnapHz(DesiredHz(profile, spec), KnownRates(spec.Slot, gdiName, width, height));

    /// <summary>The rate the user configured themselves (profile value raised by the compose override), independent of any client.</summary>
    private static int ExplicitHz(Profile profile, VirtualDisplaySpec spec)
        => profile.ComposeRefreshRate > 0 ? Math.Max(profile.ComposeRefreshRate, spec.RefreshRate) : Math.Max(1, spec.RefreshRate);

    /// <summary>
    /// Refresh rates the driver really publishes for a slot at a resolution. Read from Windows while the display is on;
    /// remembered in the state file so a stand-by display can be configured without switching it on; before the first
    /// observation the registry table is the best guess. Registered-but-unpublished rates are deliberately excluded:
    /// asking Windows or Sunshine for one of those fails (a failed Sunshine display config lands the session on the
    /// primary display) — they become usable after the re-plug that NeedsReplug() schedules.
    /// </summary>
    private List<int> KnownRates(int slot, string? gdiName, int width, int height)
    {
        var key = $"{slot}:{width}x{height}";
        if (!string.IsNullOrEmpty(gdiName))
        {
            var rates = ParsecModes.ExposedRates(gdiName, width, height);
            if (rates.Count > 0)
            {
                lock (_state)
                {
                    if (!_state.ExposedRates.TryGetValue(key, out var old) || !old.SequenceEqual(rates))
                    {
                        _state.ExposedRates[key] = rates;
                        _state.Save();
                    }
                }
                return rates;
            }
        }
        lock (_state)
        {
            if (_state.ExposedRates.TryGetValue(key, out var cached) && cached.Count > 0) return cached.ToList();
        }
        return ParsecModes.RegisteredRates(width, height);
    }

    private int DesiredHz(Profile profile, VirtualDisplaySpec spec)
    {
        var hz = Math.Max(1, spec.RefreshRate);
        var clientFps = ClientFps(spec);
        if (profile.MatchClientFps && clientFps > 0) hz = Math.Max(hz, clientFps);
        if (profile.ComposeRefreshRate > 0) hz = Math.Max(profile.ComposeRefreshRate, hz);
        return hz;
    }

    /// <summary>Frame rate the client of this display's instance asked for last (0 = unknown).</summary>
    private int ClientFps(VirtualDisplaySpec spec)
    {
        if (spec.InstanceId <= 0) return 0;
        if (_state.LastClientMode.TryGetValue(spec.InstanceId, out var mode) && mode.Fps > 0) return mode.Fps;
        if (_state.LastClientFps.TryGetValue(spec.InstanceId, out var f) && f > 0) return f;
        return 0;
    }

    public static int SnapHz(int desired, IReadOnlyCollection<int> available)
    {
        if (available.Count == 0 || available.Contains(desired)) return desired;
        var multiple = available.Where(r => r > desired && r % desired == 0).OrderBy(r => r).FirstOrDefault();
        if (multiple > 0) return multiple;
        var higher = available.Where(r => r > desired).OrderBy(r => r).FirstOrDefault();
        return higher > 0 ? higher : available.Max();
    }

    /// <summary>Refresh rate the engine will set for this spec (used by the watchdog's layout check and the UI).</summary>
    public int TargetHz(Profile profile, VirtualDisplaySpec spec, DisplayInfo? display)
    {
        var (w, h) = ResolveResolution(profile, spec, display?.GdiName);
        return ResolveHz(profile, spec, display?.GdiName, w, h);
    }

    private int ResolveHz(Profile profile, VirtualDisplaySpec spec, string? gdiName)
    {
        var (w, h) = ResolveResolution(profile, spec, gdiName);
        return ResolveHz(profile, spec, gdiName, w, h);
    }

    /// <summary>
    /// Applies the client's requested stream mode to the virtual display an instance streams (resolution + refresh rate).
    /// Returns null when nothing had to change, false when the required mode is not available on the display.
    /// </summary>
    /// <param name="instanceId">请求串流的 Sunshine 实例编号。</param>
    /// <param name="mode">客户端请求的分辨率及帧率。</param>
    public bool? ApplyClientMode(int instanceId, ClientMode mode)
    {
        // 与方案切换/重新插拔互斥，避免检查完目标后又被另一个操作移除。
        if (!_busy.Wait(0)) return null;
        try { return ApplyClientModeCore(instanceId, mode); }
        finally { _busy.Release(); }
    }

    /// <summary>在持有方案锁时匹配客户端模式，仅修改独立的虚拟屏。</summary>
    /// <param name="instanceId">请求串流的 Sunshine 实例编号。</param>
    /// <param name="mode">客户端请求的分辨率及帧率。</param>
    private bool? ApplyClientModeCore(int instanceId, ClientMode mode)
    {
        if (!_state.InstanceOutputIds.TryGetValue(instanceId, out var outputId) || string.IsNullOrWhiteSpace(outputId)) return null;
        var profile = _settings.Profile(_state.ActiveProfileId);
        if (profile == null || profile.Mode is ProfileMode.MainOnly or ProfileMode.Clone || (!profile.MatchClientFps && !profile.AutoMatchClientResolution)) return null;
        var spec = profile.VirtualDisplays.FirstOrDefault(v => v.InstanceId == instanceId);
        if (spec == null) return null;
        var displays = DisplayManager.Enumerate();
        var display = displays.FirstOrDefault(d => d.Active && d.SunshineDeviceId.Equals(outputId, StringComparison.OrdinalIgnoreCase));
        if (display == null || !display.IsParsec || !display.IsReady) return null;
        if (DisplayManager.ValidateLayoutTargets(new[] { new LayoutItem(display) }, displays) is { } targetError)
        {
            Log.Warn("拒绝匹配客户端显示模式: " + targetError);
            return false;
        }

        var (w, h) = ResolveResolution(profile, spec, display.GdiName);
        var hz = ResolveHz(profile, spec, display.GdiName, w, h);
        if (display.Width == w && display.Height == h && Math.Abs(display.RefreshRate - hz) <= 1) return null;

        if (profile.IdleOff && profile.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly)
        {
            // Sunshine's display-device module owns the mode while a session runs; only keep its manual resolution current.
            var dd = ComputeDd(profile, spec, display.GdiName);
            var rt = _instances.Get(_settings.Instance(instanceId)!);
            if (rt.DesiredDd != dd)
            {
                _instances.EnsureConfig(_settings.Instance(instanceId)!, outputId, dd, out _);
                _state.InstanceDd[instanceId] = dd;
                _state.Save();
                Log.Info($"实例 {instanceId} 的 Sunshine 显示设备配置改为 {dd}，将在实例空闲时重启生效");
            }
            return null;
        }

        if (!DisplayManager.HasMode(display.GdiName, w, h, hz))
        {
            if (ParsecModes.RegisteredRates(w, h).Contains(hz))
            {
                // registered but not published yet: the driver needs a re-plug, which must wait until nobody streams
                RequestReplug(display.ParsecSlot, $"模式 {w}x{h}@{hz} 已注册但驱动尚未发布");
                return false;
            }
            var (cw, ch) = HostResolutionFor(instanceId, mode);
            Log.Info($"客户端请求 {mode}（虚拟屏应为 {cw}x{ch}@{mode.Fps}），但 {display.GdiName} 没有这个模式。可在“虚拟屏”页注册自定义模式 {cw}x{ch}@{mode.Fps}");
            return false;
        }
        var r = DisplayManager.ApplyLayout(new[] { new LayoutItem(display) { Width = w, Height = h, RefreshRate = hz } });
        Log.Info($"客户端请求 {mode}，虚拟屏 {display.GdiName} 从 {display.ModeText} 切换到 {w}x{h}@{hz}Hz{(r.Success ? "成功" : "失败")}: {r}");
        return r.Success;
    }

    /// <summary>Refresh-rate-only variant kept for logs without the mode line (older Sunshine builds).</summary>
    public bool? ApplyClientFps(int instanceId, int fps)
    {
        var known = _state.LastClientMode.GetValueOrDefault(instanceId);
        var mode = known != null ? known with { Fps = fps } : new ClientMode(0, 0, fps);
        if (mode.Width == 0)
        {
            // no resolution knowledge: fall back to the profile resolution
            _state.LastClientFps[instanceId] = fps;
            var profile = _settings.Profile(_state.ActiveProfileId);
            var spec = profile?.VirtualDisplays.FirstOrDefault(v => v.InstanceId == instanceId);
            if (spec == null) return null;
            mode = new ClientMode(spec.Width, spec.Height, fps);
        }
        return ApplyClientMode(instanceId, mode);
    }

    /// <summary>Display-device options for the Sunshine instance streaming <paramref name="spec"/> under <paramref name="profile"/>.</summary>
    public DdOptions ComputeDd(Profile profile, VirtualDisplaySpec spec, string? gdiName)
    {
        if (!profile.IdleOff || profile.Mode is ProfileMode.Clone or ProfileMode.MainOnly) return DdOptions.Disabled;
        var (w, h) = ResolveResolution(profile, spec, gdiName);
        var hz = ResolveHz(profile, spec, gdiName, w, h);
        // "automatic" makes Sunshine ask the driver for the client's fps itself; that is only safe when the rate we
        // resolved is exactly that fps (the mode exists and nothing raises it). Otherwise pin the resolved rate so the
        // driver is never asked for a mode it does not have (the session would start on the primary display).
        var clientFps = ClientFps(spec);
        int? manualHz = profile.MatchClientFps && clientFps > 0 && hz == clientFps ? null : hz;
        var configuration = profile.Mode == ProfileMode.VirtualOnly ? "ensure_only_display" : "ensure_active";
        // Extend: a dropped connection keeps the session and the display; the hub asks before switching it off
        // (VirtualOnly keeps reverting on disconnect: the physical displays are off there, a prompt would not be seen)
        var ask = _settings.ConfirmStandbyOnDisconnect && profile.Mode == ProfileMode.Extend;
        return new DdOptions(configuration, w, h, manualHz, RevertOnDisconnect: !ask, RevertDelayMs: ask ? 500 : 3000);
    }

    /// <summary>Switches an idle virtual display off (keeps it connected). Returns true when something was detached.</summary>
    public bool DetachIdleVirtualDisplay(DisplayInfo display, string reason)
    {
        if (!display.Active || !display.IsParsec) return false;
        var r = DisplayManager.ApplyLayout(new[] { new LayoutItem(display) { Attached = false } });
        Log.Info($"副屏待机: 停用 {display.GdiName}{(r.Success ? "成功" : "失败")}（{reason}）: {r}");
        return r.Success;
    }

    /// <summary>
    /// Stores the current arrangement (virtual display positions into the active profile, physical display positions
    /// into the state) after the user rearranged displays in Windows settings. Refuses snapshots taken while Windows
    /// is mid-reconfiguration: a monitor hot-plug briefly leaves the virtual display alone at (0,0) as the primary,
    /// and remembering that is how the layout used to get "forgotten". Rules: a physical display must be active and
    /// primary, virtual displays must not overlap physical ones, and — unless <paramref name="force"/> (the user pressed
    /// "apply layout" in the hub) — the set of active displays must be the one the last apply produced.
    /// </summary>
    public bool RememberLayout(List<DisplayInfo> displays, bool force = false)
    {
        var profile = _settings.Profile(_state.ActiveProfileId);
        if (profile == null || !profile.RememberManualPosition || profile.Mode != ProfileMode.Extend) return false;
        var active = displays.Where(d => d.Active && d.IsReady).ToList();
        var physical = active.Where(d => !d.IsParsec).ToList();
        if (physical.Count == 0 || !physical.Any(d => d.Primary))
        {
            Log.Info("忽略当前布局快照：没有激活的物理主屏（显示器可能正在重新连接）");
            return false;
        }
        if (!force)
        {
            // the physical displays must be the ones the apply saw (a monitor that dropped off the DP link changes the
            // set); virtual displays come and go with stand-by, so they are not part of this comparison
            var expected = _state.LastAppliedActiveIds;
            var current = physical.Select(d => d.SunshineDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expected.Count > 0 && !current.SetEquals(expected))
            {
                Log.Info($"忽略当前布局快照：激活的物理显示器与方案生效时不同（{string.Join(",", physical.Select(d => d.Label))}）");
                return false;
            }
        }
        foreach (var v in active.Where(d => d.IsParsec))
        {
            if (physical.Any(p => Overlaps(v, p)))
            {
                Log.Info($"忽略当前布局快照：{v.GdiName} 与物理显示器重叠 ({v.PositionX},{v.PositionY})");
                return false;
            }
        }

        var changed = false;
        foreach (var spec in profile.VirtualDisplays)
        {
            var d = active.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == spec.Slot);
            if (d == null) continue;
            if (spec.Placement == Placement.Custom && spec.X == d.PositionX && spec.Y == d.PositionY) continue;
            spec.Placement = Placement.Custom;
            spec.X = d.PositionX;
            spec.Y = d.PositionY;
            changed = true;
            Log.Info($"记住虚拟屏 #{spec.Slot + 1} 的手动位置 ({d.PositionX},{d.PositionY})（方案「{profile.Name}」）");
        }
        foreach (var p in physical)
        {
            var key = p.SunshineDeviceId;
            if (string.IsNullOrEmpty(key)) continue;
            if (_state.PhysicalLayout.TryGetValue(key, out var old) && old.X == p.PositionX && old.Y == p.PositionY && old.Primary == p.Primary && old.Width == p.Width && old.Height == p.Height) continue;
            _state.PhysicalLayout[key] = new DisplayPlacement { X = p.PositionX, Y = p.PositionY, Width = p.Width, Height = p.Height, Primary = p.Primary, Label = p.Label };
            changed = true;
            Log.Info($"记住物理显示器 {p.Label} 的位置 ({p.PositionX},{p.PositionY}){(p.Primary ? " 主屏" : string.Empty)}");
        }
        if (changed)
        {
            _settings.Save();
            _state.Save();
        }
        return changed;
    }

    private static bool Overlaps(DisplayInfo a, DisplayInfo b)
        => a.PositionX < b.PositionX + b.Width && b.PositionX < a.PositionX + a.Width && a.PositionY < b.PositionY + b.Height && b.PositionY < a.PositionY + a.Height;

    private void Step(ApplyResult result, string text)
    {
        CurrentStep = text;
        result.Steps.Add(text);
        Log.Info("[apply] " + text);
        try { Progress?.Invoke(text); } catch { }
    }

    public async Task<ApplyResult> ApplyAsync(Profile profile, CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new ApplyResult { Success = false, Summary = "另一个操作正在进行，请稍候" };
        }
        try
        {
            return await Task.Run(() => ApplyCore(profile, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            CurrentStep = string.Empty;
            _busy.Release();
            try { Applied?.Invoke(); } catch { }
        }
    }

    private ApplyResult ApplyCore(Profile profile, CancellationToken ct)
    {
        var result = new ApplyResult();
        Step(result, $"开始应用方案「{profile.Name}」({profile.ModeText})");
        try
        {
            var want = profile.VirtualCount;
            var specs = profile.VirtualDisplays.OrderBy(v => v.Slot).Take(want).ToList();
            for (var i = 0; i < specs.Count; i++) specs[i].Slot = i; // slots are always contiguous 0..N-1
            // the physical displays as they are before anything gets plugged (see RestorePhysicalDisplays)
            var physicalBefore = DisplayManager.Enumerate().Where(d => !d.IsParsec && d.IsReady).ToList();

            // ---------------------------------------------------------------- 1. driver
            if (want > 0 || DisplayManager.Enumerate().Any(d => d.IsParsec))
            {
                if (!_vdd.Open())
                {
                    result.Success = false;
                    result.Summary = _vdd.LastError ?? "Parsec VDD 打开失败";
                    Step(result, "✗ " + result.Summary);
                    return result;
                }
                _vdd.PingIntervalMs = _settings.VddPingMs;
                Step(result, $"Parsec VDD 已连接 (驱动版本 0.{_vdd.Version}), keep-alive 每 {_vdd.PingIntervalMs}ms");
            }

            // ---------------------------------------------------------------- 2. reconcile virtual display count
            var standBy = profile.IdleOff && profile.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly;
            var displays = ReconcileVirtualDisplays(result, want, ct, standBy);
            if (displays == null)
            {
                result.Success = false;
                return result;
            }

            // ---------------------------------------------------------------- 3. modes
            var parsec = displays.Where(d => d.IsParsec).OrderBy(d => d.ParsecSlot).ToList();
            foreach (var spec in specs)
            {
                var d = parsec.FirstOrDefault(p => p.ParsecSlot == spec.Slot);
                if (d == null)
                {
                    Step(result, $"✗ 找不到虚拟屏槽位 {spec.Slot}");
                    result.Success = false;
                    return result;
                }
                if (!d.IsReady)
                {
                    displays = DisplayManager.WaitFor(l => l.Any(x => x.IsParsec && x.ParsecSlot == spec.Slot && x.IsReady), TimeSpan.FromSeconds(8));
                    d = displays.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == spec.Slot) ?? d;
                }
                if (!d.IsReady) continue;
                var (mw, mh) = ResolveResolution(profile, spec, d.GdiName);
                // the rate the user asked for must exist in the driver table (client-fps rates are only used when present)
                var wanted = ExplicitHz(profile, spec);
                if (!DisplayManager.HasMode(d.GdiName, mw, mh, wanted) && !ParsecModes.RegisteredRates(mw, mh).Contains(wanted))
                {
                    Step(result, $"虚拟屏 #{spec.Slot + 1} 缺少模式 {mw}x{mh}@{wanted}，注册 Parsec 自定义模式…");
                    var (ok, msg) = ParsecModes.EnsureRegistered(mw, mh, wanted);
                    Step(result, (ok ? "✓ " : "✗ ") + msg);
                }
                var hz = ResolveHz(profile, spec, d.GdiName, mw, mh);
                if (!NeedsReplug(d, mw, mh)) continue; // the table Windows sees already matches the registry
                if (IsSpecStreaming(spec))
                {
                    RequestReplug(spec.Slot, "驱动模式表已更新");
                    Step(result, $"虚拟屏 #{spec.Slot + 1} 正在串流，等客户端断开后再重新插拔以刷新模式表");
                    continue;
                }
                // The driver only publishes the registry modes for a freshly plugged monitor.
                Step(result, $"重新插拔虚拟屏 #{spec.Slot + 1} 以刷新驱动模式表");
                displays = ReplugCore(spec.Slot, result);
                d = displays.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == spec.Slot);
                if (d == null || !d.IsReady)
                {
                    Step(result, $"⚠ 虚拟屏 #{spec.Slot + 1} 重新插拔后尚未就绪");
                    continue;
                }
                hz = ResolveHz(profile, spec, d.GdiName, mw, mh);
                Step(result, DisplayManager.HasMode(d.GdiName, mw, mh, wanted)
                    ? $"✓ 虚拟屏 #{spec.Slot + 1} 模式表已更新: {string.Join(", ", ParsecModes.ExposedRates(d.GdiName, mw, mh).Select(r => r + "Hz"))}，使用 {hz}Hz"
                    : $"⚠ 虚拟屏 #{spec.Slot + 1} 仍然没有 {mw}x{mh}@{wanted}，改用 {hz}Hz");
            }

            // ---------------------------------------------------------------- 4. layout
            displays = DisplayManager.Enumerate();
            displays = EnsureParsecEnabled(result, displays);
            if (displays.Any(d => d.IsParsec && !d.IsReady))
            {
                displays = DisplayManager.WaitFor(l => l.Where(d => d.IsParsec).All(d => d.IsReady), TimeSpan.FromSeconds(8));
            }
            // "virtual only" switches the physical displays off on purpose; every other mode keeps them as they were
            if (profile.Mode != ProfileMode.VirtualOnly || profile.IdleOff)
            {
                displays = RestorePhysicalDisplays(result, physicalBefore, displays);
            }
            parsec = displays.Where(d => d.IsParsec).OrderBy(d => d.ParsecSlot).ToList();
            if (specs.Any(spec => !parsec.Any(d => d.ParsecSlot == spec.Slot &&
                    (d.IsReady || standBy && !d.Active))))
            {
                result.Success = false;
                result.Summary = "虚拟屏已断开或尚未就绪，已停止应用显示配置";
                Step(result, "✗ " + result.Summary);
                return result;
            }
            var physicalActive = displays.Where(d => !d.IsParsec && d.Active).ToList();
            var primaryPhysical = physicalActive.FirstOrDefault(d => d.Primary) ?? physicalActive.FirstOrDefault();

            switch (profile.Mode)
            {
                case ProfileMode.MainOnly:
                    Step(result, "仅主屏：虚拟屏已全部移除");
                    if (!physicalActive.Any())
                    {
                        Step(result, "没有激活的物理显示器，恢复扩展拓扑");
                        DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
                    }
                    break;

                case ProfileMode.Extend:
                    ApplyExtendLayout(result, profile, specs, parsec, primaryPhysical, displays);
                    break;

                case ProfileMode.VirtualOnly when profile.IdleOff:
                    // idle state = physical displays; Sunshine switches to "only this display" for the session
                    ApplyExtendLayout(result, profile, specs, parsec, primaryPhysical, displays);
                    Step(result, "仅副屏（待机模式）：串流时由 Sunshine 切换为只保留虚拟屏，断开后自动还原");
                    break;

                case ProfileMode.VirtualOnly:
                    ApplyVirtualOnlyLayout(result, profile, specs[0], parsec[0], displays);
                    break;

                case ProfileMode.Clone:
                    ApplyCloneLayout(result, profile, specs[0], parsec[0], primaryPhysical);
                    break;
            }
            if (!result.Success)
            {
                result.Summary = "显示布局未能安全应用，已停止更新串流配置，请待屏幕连接稳定后重试";
                Step(result, "✗ " + result.Summary);
                return result;
            }

            // ---------------------------------------------------------------- 5. Sunshine instances
            displays = DisplayManager.Enumerate();
            // present (active or stand-by) displays all have a stable device id; never drop an instance because its
            // display happens to be switched off at this moment
            parsec = displays.Where(d => d.IsParsec).OrderBy(d => d.ParsecSlot).ToList();
            var mapping = new Dictionary<int, string>();
            var ddMap = new Dictionary<int, DdOptions>();
            var slotOfInstance = new Dictionary<int, int>();
            if (profile.Mode == ProfileMode.MainOnly)
            {
                if (profile.KeepMainInstance) mapping[1] = string.Empty; // primary display
            }
            else
            {
                foreach (var spec in specs)
                {
                    if (spec.InstanceId <= 0) continue;
                    var d = parsec.FirstOrDefault(p => p.ParsecSlot == spec.Slot);
                    if (d == null || string.IsNullOrWhiteSpace(d.SunshineDeviceId) ||
                        profile.Mode != ProfileMode.Clone && d.Active &&
                        DisplayManager.ValidateLayoutTargets(new[] { new LayoutItem(d) }, displays) != null)
                    {
                        result.Success = false;
                        result.Summary = $"虚拟屏 #{spec.Slot + 1} 的抓取目标不可用，已停止更新实例";
                        Step(result, "✗ " + result.Summary);
                        return result;
                    }
                    mapping[spec.InstanceId] = d.SunshineDeviceId;
                    ddMap[spec.InstanceId] = ComputeDd(profile, spec, d.GdiName);
                    slotOfInstance[spec.InstanceId] = spec.Slot;
                }
            }
            ReconcileInstances(result, mapping, ddMap, ct);
            // 替换整张映射，避免“仅主屏”或减少副屏后残留旧的手动分辨率。
            _state.InstanceDd = mapping.Keys.ToDictionary(id => id, id => ddMap.GetValueOrDefault(id) ?? DdOptions.Disabled);

            // ---------------------------------------------------------------- 5b. idle stand-by: switch virtual displays off until a client connects
            if (profile.IdleOff && profile.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly)
            {
                _instances.PollAsync(ct).GetAwaiter().GetResult();
                var current = DisplayManager.Enumerate();
                foreach (var (id, slot) in slotOfInstance)
                {
                    var rt = _instances.Get(_settings.Instance(id)!);
                    var d = current.FirstOrDefault(x => x.IsParsec && x.ParsecSlot == slot);
                    if (d == null) continue;
                    if (rt.State == InstanceState.Streaming)
                    {
                        Step(result, $"虚拟屏 #{slot + 1} 正在被实例 {id} 串流，保持启用");
                        continue;
                    }
                    if (DetachIdleVirtualDisplay(d, "当前没有客户端连接")) Step(result, $"✓ 虚拟屏 #{slot + 1} 已进入待机（连接时自动启用）");
                }
            }

            // ---------------------------------------------------------------- 6. persist
            _state.ActiveProfileId = profile.Id;
            _state.LastAppliedUtc = DateTime.UtcNow;
            _state.LastApplySucceeded = result.Success;
            _state.ExpectedVirtualDisplays = want;
            _state.ExpectedInstances = mapping.Keys.OrderBy(k => k).ToList();
            _state.InstanceOutputIds = mapping;
            // the physical displays this apply saw (the reference for "did the user rearrange something" later)
            _state.LastAppliedActiveIds = DisplayManager.Enumerate().Where(d => d.Active && d.IsReady && !d.IsParsec).Select(d => d.SunshineDeviceId).ToList();
            _state.Save();
            _settings.ActiveProfileId = profile.Id;
            _settings.Save();

            result.Displays = DisplayManager.Enumerate();
            result.Summary = result.Success ? $"方案「{profile.Name}」已生效" : $"方案「{profile.Name}」部分步骤失败，请查看日志";
            Step(result, (result.Success ? "✓ " : "⚠ ") + result.Summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Summary = "已取消";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Summary = ex.Message;
            Log.Error("应用方案失败", ex);
            Step(result, "✗ " + ex.Message);
            return result;
        }
    }

    // ------------------------------------------------------------------ virtual displays
    private List<DisplayInfo>? ReconcileVirtualDisplays(ApplyResult result, int want, CancellationToken ct, bool standBy = false)
    {
        var displays = DisplayManager.Enumerate();
        var parsec = displays.Where(d => d.IsParsec).ToList();
        Step(result, $"当前 Parsec 虚拟屏: {parsec.Count} 块 (需要 {want})");

        if (want == 0)
        {
            if (parsec.Count > 0 && _vdd.IsOpen)
            {
                _vdd.RemoveAll();
                displays = DisplayManager.WaitFor(l => !l.Any(d => d.IsParsec), TimeSpan.FromSeconds(12));
                Step(result, displays.Any(d => d.IsParsec) ? "⚠ 部分虚拟屏没有及时消失" : "✓ 虚拟屏已全部移除");
            }
            return displays;
        }

        bool SlotsOk(List<DisplayInfo> list)
        {
            var slots = list.Where(d => d.IsParsec).Select(d => d.ParsecSlot).OrderBy(s => s).ToList();
            return slots.Count == want && slots.SequenceEqual(Enumerable.Range(0, want));
        }

        if (!SlotsOk(displays))
        {
            if (parsec.Count > want)
            {
                foreach (var extra in parsec.Where(p => p.ParsecSlot >= want || p.ParsecSlot < 0))
                {
                    Step(result, $"移除多余虚拟屏 (UID {extra.Uid})");
                    if (extra.ParsecSlot >= 0) _vdd.RemoveDisplay(extra.ParsecSlot);
                }
                displays = DisplayManager.WaitFor(l => l.Count(d => d.IsParsec) <= want, TimeSpan.FromSeconds(10));
            }

            var missing = want - displays.Count(d => d.IsParsec);
            for (var i = 0; i < missing; i++)
            {
                ct.ThrowIfCancellationRequested();
                var index = _vdd.AddDisplay();
                Step(result, $"已请求添加虚拟屏 (驱动返回槽位 {index})");
                Thread.Sleep(300);
            }
            displays = DisplayManager.WaitFor(l => l.Count(d => d.IsParsec) >= want && l.Where(d => d.IsParsec).All(d => d.IsReady || !d.Active), TimeSpan.FromSeconds(25));

            if (!SlotsOk(displays))
            {
                Step(result, "槽位不连续，重建全部虚拟屏");
                _vdd.RemoveAll();
                DisplayManager.WaitFor(l => !l.Any(d => d.IsParsec), TimeSpan.FromSeconds(12));
                for (var i = 0; i < want; i++)
                {
                    _vdd.AddDisplay();
                    Thread.Sleep(300);
                }
                displays = DisplayManager.WaitFor(SlotsOk, TimeSpan.FromSeconds(25));
                if (!SlotsOk(displays))
                {
                    Step(result, "✗ Windows 没有枚举出期望数量的 Parsec 虚拟屏");
                    result.Summary = "虚拟屏创建失败";
                    return null;
                }
            }
        }

        // make sure every virtual display is attached to the desktop (stand-by displays are re-enabled for configuration)
        displays = EnsureParsecEnabled(result, displays);
        // Windows may report an active path before the GDI name/mode is populated: settle before touching modes
        if (displays.Any(d => d.IsParsec && !d.IsReady))
        {
            displays = DisplayManager.WaitFor(l => l.Where(d => d.IsParsec).All(d => d.IsReady), TimeSpan.FromSeconds(8));
        }
        if (displays.Any(d => d.IsParsec && !d.IsReady))
        {
            if (standBy && displays.Where(d => d.IsParsec).All(d => !d.Active || d.IsReady))
            {
                // Sunshine enables the display itself when a client connects; keep going so its config gets updated
                Step(result, "⚠ 待机中的虚拟屏暂时无法启用，跳过模式/布局步骤，只更新 Sunshine 配置（连接时由 Sunshine 启用）");
                return displays;
            }
            Step(result, "✗ 虚拟屏已激活但 Windows 尚未完成枚举（无设备名/模式）");
            result.Summary = "虚拟屏枚举未完成";
            return null;
        }
        Step(result, $"✓ 虚拟屏就绪: {string.Join(", ", displays.Where(d => d.IsParsec).OrderBy(d => d.ParsecSlot).Select(d => $"#{d.ParsecSlot + 1}={d.GdiName} {d.ModeText}"))}");
        return displays;
    }

    /// <summary>
    /// Plugging a virtual display makes Windows replay the topology it saved for the new monitor set, and that entry can
    /// switch a physical display off or give it another mode (2026-10-01, laptop without the Dell: the panel went dark when
    /// the virtual display appeared, Sunshine's "enable all displays" fallback brought it back at 1024x768, and Windows
    /// saved that — after every boot). Puts the physical displays seen before this apply back: switched-off ones are
    /// re-activated, and a mode that got smaller (fewer pixels, or the same size at a lower rate) is restored. A mode
    /// that got bigger is left alone: that is a monitor recovering from a hot-plug, not a downgrade.
    /// </summary>
    private List<DisplayInfo> RestorePhysicalDisplays(ApplyResult result, List<DisplayInfo> before, List<DisplayInfo> displays)
    {
        if (before.Count == 0) return displays;
        static bool Same(DisplayInfo a, DisplayInfo b) => !string.IsNullOrEmpty(b.SunshineDeviceId)
            ? a.SunshineDeviceId.Equals(b.SunshineDeviceId, StringComparison.OrdinalIgnoreCase)
            : a.AdapterId.Same(b.AdapterId) && a.TargetId == b.TargetId;

        var off = before.Where(b => !displays.Any(d => !d.IsParsec && d.Active && Same(d, b))).ToList();
        if (off.Count > 0)
        {
            Step(result, $"插入虚拟屏后 Windows 关闭了 {string.Join("、", off.Select(d => d.Label))}，重新启用");
            var code = DisplayManager.ActivateDisplays(off, displays);
            if (code != 0) Step(result, $"⚠ 重新启用物理显示器失败 ({code})");
            displays = DisplayManager.WaitFor(l => off.All(b => l.Any(d => !d.IsParsec && d.IsReady && Same(d, b))), TimeSpan.FromSeconds(10));
        }

        var items = new List<LayoutItem>();
        foreach (var b in before)
        {
            var d = displays.FirstOrDefault(x => !x.IsParsec && x.IsReady && Same(x, b));
            if (d == null) continue;
            long now = (long)d.Width * d.Height, was = (long)b.Width * b.Height;
            var downgraded = now < was || now == was && d.RefreshRate < b.RefreshRate - 1;
            if (!downgraded) continue;
            if (!DisplayManager.HasMode(d.GdiName, b.Width, b.Height, b.RefreshRate)) continue;
            Step(result, $"{d.Label} 被改成了 {d.ModeText}，恢复为 {b.ModeText}");
            items.Add(new LayoutItem(d) { Width = b.Width, Height = b.Height, RefreshRate = b.RefreshRate });
        }
        if (items.Count == 0) return displays;
        var r = DisplayManager.ApplyLayout(items);
        if (!r.Success) Step(result, "⚠ 恢复物理显示器分辨率失败: " + r);
        return DisplayManager.Enumerate();
    }

    /// <summary>Switches connected-but-inactive Parsec displays on (explicit topology) and waits until they are enumerated.</summary>
    private List<DisplayInfo> EnsureParsecEnabled(ApplyResult result, List<DisplayInfo> displays)
    {
        var inactive = displays.Where(d => d.IsParsec && !d.Active).ToList();
        bool SharesPhysicalSource(List<DisplayInfo> current) => current.Any(v => v.IsParsec && v.Active &&
            current.Any(p => !p.IsParsec && p.Active && !string.IsNullOrEmpty(v.GdiName) &&
                p.GdiName.Equals(v.GdiName, StringComparison.OrdinalIgnoreCase)));
        var sharedSource = SharesPhysicalSource(displays);
        if (inactive.Count == 0 && !sharedSource) return displays;
        Step(result, sharedSource ? "虚拟屏与物理屏共用显示源，先切换为独立扩展显示再配置" : $"有 {inactive.Count} 块虚拟屏处于待机（停用），临时启用以便配置");
        var code = DisplayManager.ActivateDisplays(inactive, displays);
        if (code != 0)
        {
            Step(result, $"⚠ 显式启用失败 ({code})，改用扩展拓扑");
            DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
        }
        var slots = displays.Where(d => d.IsParsec).Select(d => d.ParsecSlot).ToList();
        bool AllReady(List<DisplayInfo> current) => !SharesPhysicalSource(current) &&
            slots.All(slot => current.Any(d => d.IsParsec && d.ParsecSlot == slot && d.IsReady));
        var ready = DisplayManager.WaitFor(AllReady, TimeSpan.FromSeconds(15));
        if (AllReady(ready)) Step(result, "✓ 虚拟屏已启用");
        return ready;
    }

    // ------------------------------------------------------------------ driver mode table (re-plug)
    private readonly Dictionary<int, string> _replugDone = new();

    /// <summary>True when the display should be re-plugged for the current registry table (and that was not already tried).</summary>
    private bool NeedsReplug(DisplayInfo d, int width, int height)
        => ParsecModes.NeedsReplug(d, width, height) && _replugDone.GetValueOrDefault(d.ParsecSlot) != ParsecModes.Signature();

    public bool NeedsReplug(Profile profile, VirtualDisplaySpec spec, DisplayInfo d)
    {
        var (w, h) = ResolveResolution(profile, spec, d.GdiName);
        return NeedsReplug(d, w, h);
    }

    /// <summary>True when the Sunshine instance assigned to this spec currently has a streaming session.</summary>
    public bool IsSpecStreaming(VirtualDisplaySpec spec)
    {
        if (spec.InstanceId <= 0) return false;
        var inst = _settings.Instance(spec.InstanceId);
        return inst != null && _instances.Get(inst).State == InstanceState.Streaming;
    }

    public bool IsSlotStreaming(int slot)
    {
        var profile = _settings.Profile(_state.ActiveProfileId);
        var spec = profile?.VirtualDisplays.FirstOrDefault(v => v.Slot == slot);
        return spec != null && IsSpecStreaming(spec);
    }

    public bool IsReplugPending(int slot)
    {
        lock (_state) return _state.PendingReplugSlots.Contains(slot);
    }

    /// <summary>Remember that a display must be re-plugged once its client disconnects (the watchdog does it).</summary>
    public void RequestReplug(int slot, string reason)
    {
        lock (_state)
        {
            if (_state.PendingReplugSlots.Contains(slot)) return;
            _state.PendingReplugSlots.Add(slot);
            _state.Save();
        }
        Log.Info($"虚拟屏 #{slot + 1} 需要重新插拔（{reason}），将在没有客户端串流时进行");
    }

    private void ClearPendingReplug(int slot)
    {
        lock (_state)
        {
            if (_state.PendingReplugSlots.Remove(slot)) _state.Save();
        }
    }

    /// <summary>Removes and re-adds one virtual display so the driver rebuilds its mode table from the registry. Caller holds the busy lock.</summary>
    private List<DisplayInfo> ReplugCore(int slot, ApplyResult? result)
    {
        var before = DisplayManager.Enumerate().FirstOrDefault(d => d.IsParsec && d.ParsecSlot == slot);
        var wasActive = before?.Active == true;
        _vdd.RemoveDisplay(slot);
        DisplayManager.WaitFor(l => !l.Any(x => x.IsParsec && x.ParsecSlot == slot), TimeSpan.FromSeconds(8));
        var index = _vdd.AddDisplay();
        if (index != slot) Log.Warn($"重新插拔: 驱动把虚拟屏放到了槽位 {index}（原槽位 {slot}），下次应用方案时会整理槽位");
        var displays = DisplayManager.WaitFor(l => l.Any(x => x.IsParsec && x.ParsecSlot == index), TimeSpan.FromSeconds(20));
        if (wasActive)
        {
            // Windows may restore the remembered (possibly stand-by) state for this monitor: switch it back on
            displays = EnsureParsecEnabled(result ?? new ApplyResult(), displays);
        }
        _replugDone[slot] = ParsecModes.Signature();
        ClearPendingReplug(slot);
        var after = displays.FirstOrDefault(d => d.IsParsec && d.ParsecSlot == index);
        Log.Info($"已重新插拔虚拟屏 #{slot + 1}: {(after == null ? "未枚举到" : after.Active ? $"{after.GdiName} 可用模式 {string.Join(", ", DisplayManager.GetModes(after.GdiName).Where(m => m.Orientation == 0))}" : "待机（未激活）")}");
        return displays;
    }

    /// <summary>
    /// Re-plugs one virtual display outside of an apply (after the custom mode table changed) and then re-applies the
    /// active profile so mode, position and Sunshine config match the new table. Deferred while a client streams it.
    /// </summary>
    public async Task<(bool Ok, string Message)> ReplugAsync(int slot, string reason)
    {
        if (IsSlotStreaming(slot))
        {
            RequestReplug(slot, reason);
            return (false, $"虚拟屏 #{slot + 1} 正在串流，客户端断开后自动重新插拔");
        }
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return (false, "另一个操作正在进行，请稍候");
        try
        {
            if (!_vdd.Open()) return (false, _vdd.LastError ?? "Parsec VDD 打开失败");
            if (!DisplayManager.Enumerate().Any(d => d.IsParsec && d.ParsecSlot == slot))
            {
                ClearPendingReplug(slot);
                return (false, $"虚拟屏 #{slot + 1} 不存在");
            }
            Log.Info($"重新插拔虚拟屏 #{slot + 1}: {reason}");
            CurrentStep = $"重新插拔虚拟屏 #{slot + 1}";
            await Task.Run(() => ReplugCore(slot, null)).ConfigureAwait(false);
        }
        finally
        {
            CurrentStep = string.Empty;
            _busy.Release();
        }
        var profile = _settings.Profile(_state.ActiveProfileId);
        if (profile != null && profile.VirtualCount > slot)
        {
            var r = await ApplyAsync(profile).ConfigureAwait(false);
            return (r.Success, r.Success ? $"虚拟屏 #{slot + 1} 已重新插拔，驱动模式表已更新" : r.Summary);
        }
        return (true, $"虚拟屏 #{slot + 1} 已重新插拔");
    }

    /// <summary>After the custom mode table changed: re-plug every present virtual display now, or once it is idle.</summary>
    public async Task<string> RefreshModesAsync(string reason)
    {
        var notes = new List<string>();
        foreach (var d in DisplayManager.Enumerate().Where(d => d.IsParsec && d.ParsecSlot >= 0).OrderBy(d => d.ParsecSlot).ToList())
        {
            var (_, msg) = await ReplugAsync(d.ParsecSlot, reason).ConfigureAwait(false);
            notes.Add(msg);
        }
        return notes.Count == 0 ? "当前没有虚拟屏，新模式会在下次创建虚拟屏时生效" : string.Join("；", notes);
    }

    /// <summary>The user picked a refresh rate for a virtual display: store it in the active profile and apply it.</summary>
    public async Task<(bool Ok, string Message)> SetVirtualDisplayRateAsync(int slot, int hz)
    {
        var profile = _settings.Profile(_state.ActiveProfileId);
        var spec = profile?.VirtualDisplays.FirstOrDefault(v => v.Slot == slot);
        if (profile == null || spec == null) return (false, "当前方案没有这块虚拟屏");
        spec.RefreshRate = hz;
        if (profile.ComposeRefreshRate > hz) profile.ComposeRefreshRate = 0;
        _settings.Save();
        Log.Info($"用户把虚拟屏 #{slot + 1} 的刷新率设为 {hz}Hz（记入方案「{profile.Name}」）");
        var display = DisplayManager.Enumerate().FirstOrDefault(d => d.IsParsec && d.ParsecSlot == slot);
        var (w, h) = ResolveResolution(profile, spec, display?.GdiName);
        var published = KnownRates(slot, display?.IsReady == true ? display.GdiName : null, w, h);
        var streaming = IsSpecStreaming(spec);
        var r = await ApplyAsync(profile).ConfigureAwait(false);
        if (!r.Success) return (false, r.Summary);
        var target = TargetHz(profile, spec, DisplayManager.Enumerate().FirstOrDefault(d => d.IsParsec && d.ParsecSlot == slot));
        if (!published.Contains(hz) && ParsecModes.RegisteredRates(w, h).Contains(hz))
        {
            return (true, streaming
                ? $"{hz}Hz 已记入方案；驱动要重新插拔后才提供这个模式，客户端断开后自动进行，期间先用 {target}Hz"
                : $"{hz}Hz 已记入方案，虚拟屏已重新插拔，当前 {target}Hz");
        }
        if (target != hz) return (true, $"已记入方案；因跟随客户端帧率/合成刷新率，实际使用 {target}Hz");
        return (true, $"虚拟屏 #{slot + 1} → {target}Hz");
    }

    // ------------------------------------------------------------------ layouts
    private readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public bool Overlaps(Rect o) => X < o.X + o.Width && o.X < X + Width && Y < o.Y + o.Height && o.Y < Y + Height;
    }

    private static (int X, int Y) PlaceRelative(VirtualDisplaySpec spec, Rect anchor, int cursorX, int cursorY)
    {
        return spec.Placement switch
        {
            Placement.Right => (cursorX, anchor.Y),
            Placement.Left => (anchor.X - spec.Width - (cursorX - (anchor.X + anchor.Width)), anchor.Y),
            Placement.Above => (anchor.X, anchor.Y - spec.Height),
            Placement.Below => (anchor.X, anchor.Y + anchor.Height + cursorY),
            Placement.Custom => (spec.X ?? cursorX, spec.Y ?? anchor.Y),
            _ => (cursorX, anchor.Y)
        };
    }

    private void ApplyExtendLayout(ApplyResult result, Profile profile, List<VirtualDisplaySpec> specs, List<DisplayInfo> parsec, DisplayInfo? primary, List<DisplayInfo> all)
    {
        if (primary == null)
        {
            Step(result, "⚠ 没有激活的物理显示器，虚拟屏将自行排列");
        }
        parsec = parsec.Where(p => p.IsReady).ToList();
        var items = new List<LayoutItem>();
        var physicalActive = all.Where(d => d.Active && !d.IsParsec).ToList();
        // physical displays: the arrangement the user last made (state) when every remembered display is present,
        // otherwise whatever Windows currently has
        var remembered = physicalActive.Count > 0 && physicalActive.All(d => _state.PhysicalLayout.ContainsKey(d.SunshineDeviceId))
                         && _state.PhysicalLayout.Values.Count(v => v.Primary) == 1;
        if (remembered)
        {
            var rememberedPrimary = physicalActive.FirstOrDefault(d => _state.PhysicalLayout[d.SunshineDeviceId].Primary);
            if (rememberedPrimary != null && primary != null && rememberedPrimary.GdiName != primary.GdiName)
            {
                Step(result, $"按记忆把主屏恢复为 {rememberedPrimary.Label}");
                primary = rememberedPrimary;
            }
        }
        var physicalRects = new List<Rect>();
        Rect? anchor = null;
        if (primary != null)
        {
            var pp = remembered ? _state.PhysicalLayout[primary.SunshineDeviceId] : null;
            var px = pp?.X ?? primary.PositionX;
            var py = pp?.Y ?? primary.PositionY;
            items.Add(new LayoutItem(primary) { Attached = true, Primary = true, X = px, Y = py });
            anchor = new Rect(px, py, primary.Width, primary.Height);
            physicalRects.Add(anchor.Value);
            foreach (var other in physicalActive.Where(d => d.GdiName != primary.GdiName))
            {
                var op = remembered ? _state.PhysicalLayout[other.SunshineDeviceId] : null;
                var ox = op?.X ?? other.PositionX;
                var oy = op?.Y ?? other.PositionY;
                items.Add(new LayoutItem(other) { Attached = true, X = ox, Y = oy });
                physicalRects.Add(new Rect(ox, oy, other.Width, other.Height));
            }
        }

        var cursorX = anchor.HasValue ? anchor.Value.X + anchor.Value.Width : 0;
        var belowY = 0;
        foreach (var spec in specs)
        {
            var d = parsec.FirstOrDefault(p => p.ParsecSlot == spec.Slot);
            if (d == null) continue;
            var (rw, rh) = ResolveResolution(profile, spec, d.GdiName);
            var hz = ResolveHz(profile, spec, d.GdiName, rw, rh);
            var mode = PickMode(d.GdiName, rw, rh, hz, result, spec.Slot);
            var (x, y) = anchor.HasValue ? PlaceRelative(spec, anchor.Value, cursorX, belowY) : (cursorX, 0);
            if (spec.Placement == Placement.Custom && spec.X.HasValue && spec.Y.HasValue)
            {
                // a remembered position that overlaps a physical display is a snapshot taken mid hot-plug: drop it
                var want = new Rect(spec.X.Value, spec.Y.Value, mode.Width, mode.Height);
                if (physicalRects.Any(r => r.Overlaps(want)))
                {
                    Step(result, $"⚠ 虚拟屏 #{spec.Slot + 1} 记住的位置 ({spec.X},{spec.Y}) 与物理显示器重叠，改回默认摆放（主屏右侧）");
                    spec.Placement = Placement.Right;
                    spec.X = null;
                    spec.Y = null;
                    _settings.Save();
                    (x, y) = anchor.HasValue ? PlaceRelative(spec, anchor.Value, cursorX, belowY) : (cursorX, 0);
                }
                else
                {
                    x = spec.X.Value;
                    y = spec.Y.Value;
                }
            }
            items.Add(new LayoutItem(d)
            {
                Attached = true,
                Width = mode.Width,
                Height = mode.Height,
                RefreshRate = mode.RefreshRate,
                Orientation = 0,
                X = x,
                Y = y,
                Primary = primary == null && spec.Slot == 0
            });
            if (spec.Placement == Placement.Below) belowY += mode.Height; else cursorX = x + mode.Width;
        }

        var r = DisplayManager.ApplyLayout(items);
        Step(result, (r.Success ? "✓ " : "⚠ ") + r);
        if (!r.Success)
        {
            result.Success = false;
            return;
        }

        // verify + retry once (Windows sometimes needs a second pass right after a monitor arrives)
        Thread.Sleep(800);
        var check = DisplayManager.Enumerate();
        var wrong = specs.Where(spec =>
        {
            var d = check.FirstOrDefault(p => p.IsParsec && p.ParsecSlot == spec.Slot);
            var (rw, rh) = ResolveResolution(profile, spec, d?.GdiName);
            var hz = ResolveHz(profile, spec, d?.GdiName, rw, rh);
            return d == null || !d.Active || d.Width != rw || d.Height != rh || Math.Abs(d.RefreshRate - hz) > 1;
        }).ToList();
        if (wrong.Count > 0)
        {
            Step(result, $"复核发现 {wrong.Count} 块虚拟屏模式未生效，重试一次");
            Thread.Sleep(1200);
            r = DisplayManager.ApplyLayout(items);
            Step(result, (r.Success ? "✓ " : "⚠ ") + r);
            if (!r.Success) result.Success = false;
        }
    }

    private static DisplayMode PickMode(string gdi, int width, int height, int hz, ApplyResult result, int slot)
    {
        var modes = DisplayManager.GetModes(gdi, width, height).Where(m => m.Orientation == 0).ToList();
        var exact = modes.FirstOrDefault(m => m.RefreshRate == hz);
        if (exact != null) return exact;
        var fallback = modes.OrderBy(m => Math.Abs(m.RefreshRate - hz)).FirstOrDefault()
                       ?? DisplayManager.GetModes(gdi).FirstOrDefault(m => m.Orientation == 0)
                       ?? new DisplayMode(width, height, hz, 0);
        Log.Warn($"虚拟屏 #{slot + 1} 没有 {width}x{height}@{hz}，改用 {fallback}");
        result.Steps.Add($"⚠ 虚拟屏 #{slot + 1} 改用模式 {fallback}");
        return fallback;
    }

    private void ApplyVirtualOnlyLayout(ApplyResult result, Profile profile, VirtualDisplaySpec spec, DisplayInfo parsec, List<DisplayInfo> all)
    {
        var (rw, rh) = ResolveResolution(profile, spec, parsec.GdiName);
        var hz = ResolveHz(profile, spec, parsec.GdiName, rw, rh);
        var mode = PickMode(parsec.GdiName, rw, rh, hz, result, spec.Slot);

        // 1) mode first, while the display is still part of the extended desktop
        var pre = DisplayManager.ApplyLayout(new[]
        {
            new LayoutItem(parsec) { Attached = true, Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate, Orientation = 0 }
        });
        Step(result, (pre.Success ? "✓ " : "⚠ ") + "虚拟屏模式: " + pre);
        if (!pre.Success) { result.Success = false; return; }

        // 2) activate only the virtual display
        var code = DisplayManager.SetActiveTargets(new[] { (parsec.AdapterId, parsec.TargetId) });
        if (code != 0)
        {
            Step(result, $"⚠ SetDisplayConfig(仅虚拟屏) 返回 {code}，改用逐个断开物理屏");
            var items = all.Where(d => d.Active && !d.IsParsec).Select(d => new LayoutItem(d) { Attached = false }).ToList();
            items.Add(new LayoutItem(parsec) { Attached = true, Primary = true, Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate, Orientation = 0, X = 0, Y = 0 });
            var r = DisplayManager.ApplyLayout(items);
            Step(result, (r.Success ? "✓ " : "✗ ") + r);
            if (!r.Success) result.Success = false;
        }
        else
        {
            Step(result, "✓ 已切换为仅虚拟屏拓扑");
        }

        var after = DisplayManager.WaitFor(l => l.Where(d => d.Active).All(d => d.IsParsec) && l.Any(d => d.Active && d.IsParsec), TimeSpan.FromSeconds(12));
        var v = after.FirstOrDefault(d => d.IsParsec && d.Active);
        if (v == null)
        {
            Step(result, "✗ 虚拟屏没有成为唯一激活显示器，恢复扩展拓扑");
            DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
            result.Success = false;
            return;
        }
        if (v.Width != mode.Width || v.Height != mode.Height || Math.Abs(v.RefreshRate - mode.RefreshRate) > 1)
        {
            var fix = DisplayManager.ApplyLayout(new[]
            {
                new LayoutItem(v) { Attached = true, Primary = true, Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate, Orientation = 0, X = 0, Y = 0 }
            });
            Step(result, (fix.Success ? "✓ " : "⚠ ") + "重新应用虚拟屏模式: " + fix);
            if (!fix.Success) { result.Success = false; return; }
        }
        Step(result, $"✓ 仅副屏模式: {v.GdiName} {v.ModeText}");
    }

    private void ApplyCloneLayout(ApplyResult result, Profile profile, VirtualDisplaySpec spec, DisplayInfo parsec, DisplayInfo? primary)
    {
        var (rw, rh) = ResolveResolution(profile, spec, parsec.GdiName);
        var hz = ResolveHz(profile, spec, parsec.GdiName, rw, rh);
        var mode = PickMode(parsec.GdiName, rw, rh, hz, result, spec.Slot);
        var pre = DisplayManager.ApplyLayout(new[]
        {
            new LayoutItem(parsec) { Attached = true, Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate, Orientation = 0 }
        });
        Step(result, (pre.Success ? "✓ " : "⚠ ") + "虚拟屏模式: " + pre);
        if (!pre.Success) { result.Success = false; return; }
        var code = DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_CLONE);
        if (code != 0)
        {
            Step(result, $"✗ 切换复制拓扑失败 ({code})");
            result.Success = false;
            return;
        }
        DisplayManager.WaitFor(l => l.Count(d => d.Active) >= 2, TimeSpan.FromSeconds(10));
        Step(result, "✓ 复制模式已启用" + (primary != null ? $"（源: {primary.Label}）" : string.Empty));
    }

    // ------------------------------------------------------------------ sunshine
    private void ReconcileInstances(ApplyResult result, Dictionary<int, string> mapping, Dictionary<int, DdOptions> ddMap, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var spec in _settings.Instances.OrderBy(i => i.Id))
        {
            var rt = _instances.Get(spec);
            if (mapping.TryGetValue(spec.Id, out var outputId) && spec.Enabled)
            {
                var previousOutput = rt.DesiredOutputId ?? string.Empty;
                var changed = _instances.EnsureConfig(spec, outputId, ddMap.GetValueOrDefault(spec.Id) ?? DdOptions.Disabled, out var msg);
                var alive = rt.IsAlive || _instances.Adopt(spec);
                var target = string.IsNullOrEmpty(outputId) ? "主屏" : outputId;
                if (alive && !changed)
                {
                    Step(result, $"实例 {spec.Id} ({spec.Name}, 端口 {spec.Port}) 已在运行，抓取 {target}");
                    continue;
                }
                var sameOutput = string.Equals(previousOutput, outputId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                if (alive && sameOutput && (rt.State == InstanceState.Streaming || _instances.StreamingClientAddresses(spec).Count > 0))
                {
                    // never cut a running session for a display-device option change: EnsureConfig marked the runtime
                    // ConfigDirty and the watchdog restarts the instance once the client is gone
                    Step(result, $"实例 {spec.Id} 配置已更新，但正在串流：客户端断开后再重启生效");
                    continue;
                }
                Step(result, alive ? $"实例 {spec.Id} 配置变化，重启以抓取 {target}" : $"启动实例 {spec.Id} ({spec.Name}, 端口 {spec.Port}) 抓取 {target}");
                tasks.Add(Task.Run(async () =>
                {
                    // credentials first (the instance is about to be (re)started anyway, so no extra disruption)
                    await _instances.EnsureCredentialsAsync(spec, allowRestart: true).ConfigureAwait(false);
                    var ok = alive ? await _instances.RestartAsync(spec, ct).ConfigureAwait(false) : await _instances.StartAsync(spec, ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        result.Success = false;
                        Step(result, $"✗ 实例 {spec.Id} 未能就绪: {rt.LastError}");
                    }
                }, ct));
            }
            else if (rt.IsAlive || _instances.Adopt(spec))
            {
                Step(result, $"停止不再需要的实例 {spec.Id}");
                tasks.Add(_instances.StopAsync(spec));
            }
        }
        Task.WaitAll(tasks.ToArray(), ct);

        // capture-target verification from the Sunshine logs
        foreach (var (id, outputId) in mapping)
        {
            if (string.IsNullOrEmpty(outputId)) continue;
            var spec = _settings.Instance(id);
            if (spec == null) continue;
            Thread.Sleep(600);
            var verified = _instances.VerifyCaptureTarget(spec, outputId);
            Step(result, verified switch
            {
                true => $"✓ 实例 {id} 日志确认虚拟屏 {outputId} 可用",
                false => $"⚠ 实例 {id} 日志中的显示设备列表不包含 {outputId}（可能刚启动，稍后由守护复核）",
                _ => $"实例 {id} 日志尚未输出显示设备列表"
            });
        }
    }

    // ------------------------------------------------------------------ light-weight checks for the watchdog
    public sealed record HealthReport(bool VirtualDisplaysOk, int ParsecActive, int ParsecPresent, int ParsecExpected, List<int> MissingInstances, List<int> DriftedInstances, List<DisplayInfo> Displays, bool IdleOff);

    public HealthReport CheckHealth()
    {
        var displays = DisplayManager.Enumerate();
        var profile = _settings.Profile(_state.ActiveProfileId);
        var idleOff = profile != null && profile.IdleOff && profile.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly;
        var parsecActive = displays.Count(d => d.IsParsec && d.Active);
        var parsecPresent = displays.Count(d => d.IsParsec);
        var expected = _state.ExpectedVirtualDisplays;
        var missing = new List<int>();
        var drifted = new List<int>();
        foreach (var id in _state.ExpectedInstances)
        {
            var spec = _settings.Instance(id);
            if (spec == null || !spec.Enabled) continue;
            var rt = _instances.Get(spec);
            if (!rt.IsAlive && !_instances.Adopt(spec)) missing.Add(id);
            if (_state.InstanceOutputIds.TryGetValue(id, out var outputId) && !string.IsNullOrEmpty(outputId))
            {
                var d = displays.FirstOrDefault(x => x.SunshineDeviceId.Equals(outputId, StringComparison.OrdinalIgnoreCase));
                // in stand-by mode the display is allowed to be present-but-inactive
                if (d == null || (!d.Active && !idleOff)) drifted.Add(id);
            }
        }
        var ok = idleOff ? parsecPresent >= expected : parsecActive >= expected;
        return new HealthReport(ok, parsecActive, parsecPresent, expected, missing, drifted, displays, idleOff);
    }
}
