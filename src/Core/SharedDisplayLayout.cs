using System.Text.Json;

namespace MoonlightHub.Core;

public sealed record SharedMonitor(string Owner, string Id, string Name, int X, int Y, int NativeX, int NativeY,
    int Width, int Height, bool Primary, bool Virtual, bool Active, int WindowsScalePercent = 100, int ContentScalePercent = 100,
    bool ScalePending = false, int VirtualSlot = -1, bool CanScale = false, int RequestedWidth = 0, int RequestedHeight = 0)
{
    public int CanvasWidth => Math.Max(1, (int)Math.Round((ScalePending && RequestedWidth > 0 ? RequestedWidth : Width) * 100.0 / Math.Max(1, WindowsScalePercent)));
    public int CanvasHeight => Math.Max(1, (int)Math.Round((ScalePending && RequestedHeight > 0 ? RequestedHeight : Height) * 100.0 / Math.Max(1, WindowsScalePercent)));
}
public sealed class SharedDisplayDocument
{
    public long Revision { get; set; }
    public string Server { get; set; } = string.Empty;
    public List<SharedMonitor> Monitors { get; set; } = new();
}
public sealed record MonitorPlacement(string Id, int X, int Y, int Width, int Height);

/// <summary>两台 Windows 使用同一份以实际桌面像素为单位的布局，显示器用稳定设备 ID 区分。</summary>
public static class SharedDisplayLayout
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string FilePath => Path.Combine(Paths.DataDir, "shared-display-layout.json");
    public static string TopologyPath => Path.Combine(Paths.DataDir, "deskflow-monitor-topology.json");
    private static SharedDisplayDocument? _current;

    /// <param name="hub">显示器所属的运行中 Hub。</param>
    public static List<SharedMonitor> LocalMonitors(Hub hub)
    {
        if (!OperatingSystem.IsWindows()) return new();
        var owner = hub.Deskflow.LocalScreenName;
        var profile = hub.ActiveProfile;
        var speeds = hub.PointerSpeed.Screens;
        return DisplayManager.Enumerate().Where(d => d.IsReady || d.IsParsec).Select(d =>
        {
            var spec = d.IsParsec ? profile?.VirtualDisplays.FirstOrDefault(v => v.Slot == d.ParsecSlot) : null;
            var size = d.IsReady ? (d.Width, d.Height) : spec != null ? hub.Engine.TargetResolution(profile!, spec, d) : (0, 0);
            var requested = spec != null ? hub.Engine.TargetResolution(profile!, spec, d) : size;
            var id = string.IsNullOrWhiteSpace(d.SunshineDeviceId) ? d.Key : d.SunshineDeviceId;
            var previous = hub.Deskflow.SharedDisplays?.Monitors.FirstOrDefault(m => m.Owner == owner && m.Id == id);
            var dpi = speeds.FirstOrDefault(s => s.Key == "monitor:" + d.SunshineDeviceId)?.Scale ?? (previous?.WindowsScalePercent / 100.0 ?? 1);
            var x = d.IsReady ? d.PositionX : spec?.X ?? 0;
            var y = d.IsReady ? d.PositionY : spec?.Y ?? 0;
            return new SharedMonitor(owner, id, d.Label, x, y, x, y, size.Item1, size.Item2, d.Primary,
                d.IsParsec, d.Active, (int)Math.Round(dpi * 100), spec?.ContentScalePercent ?? 100,
                spec != null && hub.State.PendingReplugSlots.Contains(spec.Slot), d.IsParsec ? d.ParsecSlot : -1,
                spec != null && profile?.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly, requested.Item1, requested.Item2);
        }).Where(m => m.Width > 0 && m.Height > 0).ToList();
    }

    /// <param name="document">已有共享布局。</param>
    /// <param name="monitors">一台电脑的实时显示器快照。</param>
    /// <param name="fallbackX">第一次发现该电脑时的画布横坐标。</param>
    /// <param name="fallbackY">第一次发现该电脑时的画布纵坐标。</param>
    public static bool Merge(SharedDisplayDocument document, IReadOnlyList<SharedMonitor> monitors, int fallbackX, int fallbackY)
    {
        if (monitors.Count == 0) return false;
        var owner = monitors[0].Owner;
        var prior = document.Monitors.Where(m => m.Owner == owner).ToList();
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        var oldPrimary = prior.FirstOrDefault(m => m.Primary) ?? prior.FirstOrDefault(m => m.Id == primary.Id);
        var anchorX = oldPrimary?.X ?? fallbackX;
        var anchorY = oldPrimary?.Y ?? fallbackY;
        var updated = monitors.Select(m =>
        {
            var old = prior.FirstOrDefault(p => p.Id == m.Id);
            // Hub 自己保存的逻辑摆放不被 Windows 的物理像素坐标覆盖。
            if (old != null && old.NativeX == m.NativeX && old.NativeY == m.NativeY)
                return m with { X = old.X + anchorX - (oldPrimary?.X ?? anchorX), Y = old.Y + anchorY - (oldPrimary?.Y ?? anchorY) };
            return m with { X = anchorX + ProjectAxis(m.NativeX - primary.NativeX, primary.Width, m.Width, primary.CanvasWidth, m.CanvasWidth),
                Y = anchorY + ProjectAxis(m.NativeY - primary.NativeY, primary.Height, m.Height, primary.CanvasHeight, m.CanvasHeight) };
        }).ToList();
        if (prior.SequenceEqual(updated)) return false;
        document.Monitors.RemoveAll(m => m.Owner == owner);
        document.Monitors.AddRange(updated);
        return true;
    }

    /// <param name="hub">当前作为 Deskflow 服务端的 Hub。</param>
    public static async Task<SharedDisplayDocument> SnapshotAsync(Hub hub)
    {
        var local = LocalMonitors(hub);
        PeerHubInfo? peer = null;
        try { peer = await PeerLink.GetStatusAsync(hub.Deskflow.Settings.RemoteHost, TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { }
        lock (Gate)
        {
            _current ??= File.Exists(FilePath) ? JsonSerializer.Deserialize<SharedDisplayDocument>(File.ReadAllText(FilePath), Json) : new();
            _current ??= new();
            _current.Server = hub.Deskflow.LocalScreenName;
            var changed = Merge(_current, local, 0, 0);
            if (peer?.Monitors is { Count: > 0 } other)
            {
                var placement = hub.Deskflow.Layout is { } legacy
                    ? DeskflowPeer.MapPeers(legacy, local.Where(m => m.Active).Select(m => new LocalRect(m.Id, m.NativeX, m.NativeY, m.Width, m.Height, m.Primary)).ToList()).FirstOrDefault(p => p.Name == peer.Screen)
                    : null;
                changed |= Merge(_current, other, placement?.X ?? local.Max(m => m.X + m.Width), placement?.Y ?? 0);
            }
            if (changed || _current.Revision == 0) { _current.Revision = DateTime.UtcNow.Ticks; Save(_current); }
            return Clone(_current);
        }
    }

    public static SharedDisplayDocument Clone(SharedDisplayDocument document)
        => new() { Revision = document.Revision, Server = document.Server, Monitors = document.Monitors.ToList() };

    /// <param name="offset">当前坐标系中相对主屏的偏移。</param>
    /// <param name="parentSize">当前坐标系中主屏的尺寸。</param>
    /// <param name="childSize">当前坐标系中副屏的尺寸。</param>
    /// <param name="targetParentSize">目标坐标系中主屏的尺寸。</param>
    /// <param name="targetChildSize">目标坐标系中副屏的尺寸。</param>
    public static int ProjectAxis(int offset, int parentSize, int childSize, int targetParentSize, int targetChildSize)
    {
        if (parentSize <= 0 || childSize <= 0) throw new ArgumentOutOfRangeException(nameof(parentSize));
        if (offset >= parentSize) return targetParentSize + (int)Math.Round((offset - parentSize) * (double)targetParentSize / parentSize);
        if (offset + childSize <= 0) return -targetChildSize - (int)Math.Round((-offset - childSize) * (double)targetParentSize / parentSize);
        var range = parentSize - childSize;
        if (offset == 0) return 0;
        if (range != 0 && (double)offset / range is >= 0 and <= 1)
            return (int)Math.Round(offset * (double)(targetParentSize - targetChildSize) / range);
        return (int)Math.Round(offset * (double)targetParentSize / parentSize);
    }

    private static void Save(SharedDisplayDocument document)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, Json));
        var temp = TopologyPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        File.Move(temp, TopologyPath, true);
    }

    /// <param name="document">服务端确认的共享布局，在角色切换后继续使用。</param>
    public static void CacheDocument(SharedDisplayDocument document)
    {
        lock (Gate) { _current = Clone(document); Save(_current); }
    }

    /// <param name="hub">拥有这些显示器的 Hub。</param>
    /// <param name="positions">稳定 ID、期望分辨率和新的 Windows 坐标。</param>
    public static async Task<List<SharedMonitor>> ApplyLocalAsync(Hub hub, IReadOnlyList<MonitorPlacement> positions)
    {
        var displays = DisplayManager.Enumerate();
        var items = new List<LayoutItem>();
        var changed = false;
        var inactive = new List<(VirtualDisplaySpec Spec, int X, int Y)>();
        foreach (var pos in positions)
        {
            var display = displays.FirstOrDefault(d => (string.IsNullOrEmpty(d.SunshineDeviceId) ? d.Key : d.SunshineDeviceId) == pos.Id)
                ?? throw new InvalidOperationException("显示器已经断开，请刷新布局");
            if (display.IsReady)
            {
                if (display.Width != pos.Width || display.Height != pos.Height) throw new InvalidOperationException("显示器分辨率已变化，请刷新布局");
                items.Add(new LayoutItem(display) { Attached = true, Primary = display.Primary, X = pos.X, Y = pos.Y });
                changed |= pos.X != display.PositionX || pos.Y != display.PositionY;
            }
            else
            {
                var spec = hub.ActiveProfile?.VirtualDisplays.FirstOrDefault(v => display.IsParsec && v.Slot == display.ParsecSlot)
                    ?? throw new InvalidOperationException("该显示器当前不可调整");
                inactive.Add((spec, pos.X, pos.Y));
            }
        }
        if (changed)
        {
            var applied = DisplayManager.ApplyLayout(items);
            if (!applied.Success) throw new InvalidOperationException(applied.ToString());
            await Task.Delay(600).ConfigureAwait(false);
            hub.Engine.RememberLayout(DisplayManager.Enumerate(), force: true);
        }
        foreach (var (spec, x, y) in inactive) { spec.X = x; spec.Y = y; spec.Placement = Placement.Custom; }
        if (inactive.Count > 0) hub.Settings.Save();
        return LocalMonitors(hub);
    }

    /// <param name="hub">服务端 Hub，负责统一布局提交。</param>
    /// <param name="desired">带版本号的画布草稿。</param>
    public static async Task<SharedDisplayDocument> ApplyAsync(Hub hub, SharedDisplayDocument desired)
    {
        await ApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await SnapshotAsync(hub).ConfigureAwait(false);
            if (current.Revision != desired.Revision) throw new InvalidOperationException("布局已更新，请刷新后再应用");
            if (desired.Monitors.Count != current.Monitors.Count || desired.Monitors.Any(m => !current.Monitors.Any(old => old.Id == m.Id && old.Owner == m.Owner && old.Width == m.Width && old.Height == m.Height)))
                throw new InvalidOperationException("显示器或分辨率已变化，请刷新布局");
            if (desired.Monitors.Select(m => (m.Owner, m.Id)).Distinct().Count() != desired.Monitors.Count ||
                desired.Monitors.Any(m => Math.Abs((long)m.X) > 1000000 || Math.Abs((long)m.Y) > 1000000))
                throw new InvalidOperationException("显示器位置无效");
            foreach (var group in desired.Monitors.GroupBy(m => m.Owner))
            {
                var primary = group.FirstOrDefault(m => m.Primary) ?? group.First();
                var positions = group.Select(m => new MonitorPlacement(m.Id,
                    primary.NativeX + ProjectAxis(m.X - primary.X, primary.CanvasWidth, m.CanvasWidth, primary.Width, m.Width),
                    primary.NativeY + ProjectAxis(m.Y - primary.Y, primary.CanvasHeight, m.CanvasHeight, primary.Height, m.Height), m.Width, m.Height)).ToList();
                var live = group.Key == hub.Deskflow.LocalScreenName
                    ? await ApplyLocalAsync(hub, positions).ConfigureAwait(false)
                    : await PeerLink.ApplyMonitorPositionsAsync(hub.Deskflow.Settings.RemoteHost, positions).ConfigureAwait(false);
                var actualPrimary = live.FirstOrDefault(m => m.Primary) ?? live.First();
                foreach (var monitor in live)
                {
                    var index = desired.Monitors.FindIndex(m => m.Owner == monitor.Owner && m.Id == monitor.Id);
                    if (index >= 0) desired.Monitors[index] = monitor with { X = desired.Monitors[index].X, Y = desired.Monitors[index].Y };
                }
            }
            lock (Gate) { desired.Revision = DateTime.UtcNow.Ticks; _current = Clone(desired); Save(_current); }
            return desired;
        }
        finally { ApplyGate.Release(); }
    }
}
