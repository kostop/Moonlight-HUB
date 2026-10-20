using System.Text.Json;

namespace MoonlightHub.Core;

public sealed record SharedMonitor(string Owner, string Id, string Name, int X, int Y, int NativeX, int NativeY,
    int Width, int Height, bool Primary, bool Virtual, bool Active, int WindowsScalePercent = 100, int ContentScalePercent = 100,
    bool ScalePending = false, int VirtualSlot = -1, bool CanScale = false, int RequestedWidth = 0, int RequestedHeight = 0,
    int BaseDpiPercent = 0)
{
    public int ScalePercent => WindowsScalePercent;
    public int MaximumScalePercent { get; init; } = 500;
    public int CanvasWidth => Math.Max(1, (int)Math.Round((ScalePending && RequestedWidth > 0 ? RequestedWidth : Width) * 100.0 / Math.Max(1, WindowsScalePercent)));
    public int CanvasHeight => Math.Max(1, (int)Math.Round((ScalePending && RequestedHeight > 0 ? RequestedHeight : Height) * 100.0 / Math.Max(1, WindowsScalePercent)));
}

/// <summary>布局画布中的矩形，坐标和尺寸均使用缩放后的逻辑桌面单位。</summary>
public readonly record struct SharedLayoutRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <param name="other">另一块待比较屏幕。</param>
    public bool Overlaps(SharedLayoutRect other)
        => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    /// <param name="other">另一块待比较屏幕。</param>
    public bool Touches(SharedLayoutRect other)
        => (Right == other.X || other.Right == X) && Math.Min(Bottom, other.Bottom) > Math.Max(Y, other.Y) ||
           (Bottom == other.Y || other.Bottom == Y) && Math.Min(Right, other.Right) > Math.Max(X, other.X);
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
            var actualDpi = d.IsReady ? PointerSpeedManager.GetWindowsScalePercent(d.GdiName) : spec?.ContentScaleCurrentDpiPercent ?? 0;
            if (actualDpi > 0) dpi = actualDpi / 100.0;
            var x = d.IsReady ? d.PositionX : spec?.X ?? 0;
            var y = d.IsReady ? d.PositionY : spec?.Y ?? 0;
            var maximum = 500;
            if (d.IsParsec && d.IsReady) { try { maximum = DisplayDpiScale.Read(d).Maximum; } catch { maximum = (int)Math.Round(dpi * 100); } }
            return new SharedMonitor(owner, id, d.Label, x, y, x, y, size.Item1, size.Item2, d.Primary,
                d.IsParsec, d.Active, (int)Math.Round(dpi * 100), spec?.ContentScalePercent ?? 100,
                false, d.IsParsec ? d.ParsecSlot : -1,
                d.IsReady && spec != null && profile?.Mode is ProfileMode.Extend or ProfileMode.VirtualOnly, requested.Item1, requested.Item2,
                0) { MaximumScalePercent = maximum };
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
        var otherOwners = document.Monitors.Where(m => m.Owner != owner).ToList();
        var updated = monitors.Select(m =>
        {
            var old = prior.FirstOrDefault(p => p.Id == m.Id);
            // Hub 自己保存的逻辑摆放不被 Windows 的物理像素坐标覆盖。
            if (old != null && old.NativeX == m.NativeX && old.NativeY == m.NativeY)
                return m with { X = old.X + anchorX - (oldPrimary?.X ?? anchorX), Y = old.Y + anchorY - (oldPrimary?.Y ?? anchorY) };
            return m with { X = anchorX + ProjectAxis(m.NativeX - primary.NativeX, primary.Width, m.Width, primary.CanvasWidth, m.CanvasWidth),
                Y = anchorY + ProjectAxis(m.NativeY - primary.NativeY, primary.Height, m.Height, primary.CanvasHeight, m.CanvasHeight) };
        }).ToList();
        // 首次发现这台电脑时沿用 Windows 的物理排列；之后再新增屏幕才放到现有区域的右下空位。
        if (prior.Count > 0)
        {
            for (var index = 0; index < updated.Count; index++)
            {
                if (prior.Any(p => p.Id == updated[index].Id)) continue;
                var occupied = updated.Where((_, i) => i != index).Concat(otherOwners).ToList();
                var (x, y) = FindFreePosition(updated.Where((_, i) => i != index).ToList(), occupied,
                    updated[index].CanvasWidth, updated[index].CanvasHeight, anchorX, anchorY);
                updated[index] = updated[index] with { X = x, Y = y };
            }
            // 旧布局若已经重叠，只移动后出现的那一块，不用重排整台电脑。
            for (var index = 1; index < updated.Count; index++)
            {
                var box = Rect(updated[index]);
                if (!updated.Take(index).Concat(otherOwners).Any(m => Rect(m).Overlaps(box))) continue;
                var occupied = updated.Where((_, i) => i != index).Concat(otherOwners).ToList();
                var (x, y) = FindFreePosition(updated.Where((_, i) => i != index).ToList(), occupied,
                    updated[index].CanvasWidth, updated[index].CanvasHeight, anchorX, anchorY);
                updated[index] = updated[index] with { X = x, Y = y };
            }
        }
        if (prior.Any(old => monitors.All(m => m.Id != old.Id)))
            CompactOwner(updated, owner, primary.Id, otherOwners);
        if (prior.SequenceEqual(updated)) return false;
        document.Monitors.RemoveAll(m => m.Owner == owner);
        document.Monitors.AddRange(updated);
        return true;
    }

    /// <param name="monitors">同一台电脑的待摆放屏幕。</param>
    /// <param name="occupied">所有电脑中已经占用的屏幕位置。</param>
    /// <param name="width">新屏幕逻辑宽度。</param>
    /// <param name="height">新屏幕逻辑高度。</param>
    /// <param name="fallbackX">该电脑尚无屏幕时的横坐标。</param>
    /// <param name="fallbackY">该电脑尚无屏幕时的纵坐标。</param>
    public static (int X, int Y) FindFreePosition(IReadOnlyList<SharedMonitor> monitors, IReadOnlyList<SharedMonitor> occupied,
        int width, int height, int fallbackX = 0, int fallbackY = 0)
        => FindFreePosition(monitors.Select(Rect).ToList(), occupied.Select(Rect).ToList(), width, height, fallbackX, fallbackY);

    /// <param name="monitors">同一台电脑的待摆放矩形。</param>
    /// <param name="occupied">所有电脑中已经占用的矩形。</param>
    /// <param name="width">新屏幕逻辑宽度。</param>
    /// <param name="height">新屏幕逻辑高度。</param>
    /// <param name="fallbackX">该电脑尚无屏幕时的横坐标。</param>
    /// <param name="fallbackY">该电脑尚无屏幕时的纵坐标。</param>
    public static (int X, int Y) FindFreePosition(IReadOnlyList<SharedLayoutRect> monitors, IReadOnlyList<SharedLayoutRect> occupied,
        int width, int height, int fallbackX = 0, int fallbackY = 0)
    {
        if (monitors.Count == 0)
        {
            if (!OverlapsAny(fallbackX, fallbackY, width, height, occupied)) return (fallbackX, fallbackY);
            var edge = occupied.Count == 0 ? fallbackX : occupied.Max(m => m.Right);
            for (var scanY = fallbackY; scanY < fallbackY + 20000; scanY += 20)
                if (!OverlapsAny(edge, scanY, width, height, occupied)) return (edge, scanY);
            return (edge, fallbackY);
        }
        var right = monitors.Max(m => m.Right);
        var bottom = monitors.Max(m => m.Bottom);
        var target = (X: right, Y: bottom - height);
        var candidates = new List<(int X, int Y)> { target, (right - width, bottom) };
        foreach (var monitor in monitors)
        {
            candidates.Add((monitor.Right, ClampStart(target.Y, monitor.Y, monitor.Bottom - height)));
            candidates.Add((ClampStart(target.X - width, monitor.X, monitor.Right - width), monitor.Bottom));
        }
        foreach (var candidate in candidates.Distinct()
                     .OrderBy(c => Math.Abs(c.X - target.X) + Math.Abs(c.Y - target.Y)).ThenByDescending(c => c.X).ThenByDescending(c => c.Y))
        {
            if (!OverlapsAny(candidate.X, candidate.Y, width, height, occupied)) return candidate;
        }
        // 右下区域被另一台电脑占满时，沿下方继续寻找最近的整块空位。
        var x = right;
        var y = target.Y;
        for (var guard = 0; guard < 512; guard++)
        {
            if (!OverlapsAny(x, y, width, height, occupied)) return (x, y);
            y += 20;
        }
        return (right, bottom);
    }

    /// <param name="monitors">全部屏幕，方法只移动指定电脑的屏幕。</param>
    /// <param name="owner">需要收拢间隔的电脑名。</param>
    /// <param name="primaryId">保持不动的本机主屏稳定 ID。</param>
    /// <param name="occupied">其它电脑的屏幕，收拢时不能压到它们。</param>
    public static void CompactOwner(List<SharedMonitor> monitors, string owner, string primaryId,
        IReadOnlyList<SharedMonitor>? occupied = null)
    {
        var ownerIndexes = monitors.Select((m, i) => (m, i)).Where(t => t.m.Owner == owner).ToList();
        if (ownerIndexes.Count < 2) return;
        var primary = ownerIndexes.FirstOrDefault(t => t.m.Id == primaryId).m ?? ownerIndexes[0].m;
        for (var guard = 0; guard < ownerIndexes.Count; guard++)
        {
            var ownerItems = ownerIndexes.Select(t => monitors[t.i]).ToList();
            var baseIds = ConnectedIds(ownerItems, primary.Id);
            var rest = ownerItems.Where(m => !baseIds.Contains(m.Id)).ToList();
            if (rest.Count == 0) return;
            var movingIds = ConnectedIds(rest, rest[0].Id);
            var moving = ownerItems.Where(m => movingIds.Contains(m.Id)).ToList();
            var fixedItems = monitors.Where(m => !movingIds.Contains(m.Id)).Concat(occupied ?? []).ToList();
            var shift = FindCompactShift(moving, ownerItems.Where(m => baseIds.Contains(m.Id)).ToList(), fixedItems);
            if (shift is not { } delta || delta.Dx == 0 && delta.Dy == 0) return;
            for (var i = 0; i < monitors.Count; i++)
                if (movingIds.Contains(monitors[i].Id))
                    monitors[i] = monitors[i] with { X = monitors[i].X + delta.Dx, Y = monitors[i].Y + delta.Dy };
        }
    }

    /// <param name="monitors">需要检查的屏幕集合。</param>
    /// <param name="first">第一块重叠屏幕。</param>
    /// <param name="second">第二块重叠屏幕。</param>
    public static bool TryFindOverlap(IReadOnlyList<SharedMonitor> monitors, out SharedMonitor? first, out SharedMonitor? second)
    {
        for (var i = 0; i < monitors.Count; i++)
            for (var j = i + 1; j < monitors.Count; j++)
                if (Rect(monitors[i]).Overlaps(Rect(monitors[j])))
                {
                    first = monitors[i]; second = monitors[j];
                    return true;
                }
        first = second = null;
        return false;
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

    /// <param name="monitor">逻辑画布中的显示器。</param>
    private static SharedLayoutRect Rect(SharedMonitor monitor)
        => new(monitor.X, monitor.Y, monitor.CanvasWidth, monitor.CanvasHeight);

    /// <param name="value">希望放置的起点。</param>
    /// <param name="minimum">允许的最小值。</param>
    /// <param name="maximum">允许的最大值。</param>
    private static int ClampStart(int value, int minimum, int maximum)
        => maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    /// <param name="x">待检查横坐标。</param>
    /// <param name="y">待检查纵坐标。</param>
    /// <param name="width">待检查宽度。</param>
    /// <param name="height">待检查高度。</param>
    /// <param name="occupied">已经占用的屏幕。</param>
    private static bool OverlapsAny(int x, int y, int width, int height, IReadOnlyList<SharedMonitor> occupied)
        => OverlapsAny(x, y, width, height, occupied.Select(Rect).ToList());

    /// <param name="x">待检查横坐标。</param>
    /// <param name="y">待检查纵坐标。</param>
    /// <param name="width">待检查宽度。</param>
    /// <param name="height">待检查高度。</param>
    /// <param name="occupied">已经占用的矩形。</param>
    private static bool OverlapsAny(int x, int y, int width, int height, IReadOnlyList<SharedLayoutRect> occupied)
    {
        var candidate = new SharedLayoutRect(x, y, width, height);
        return occupied.Any(candidate.Overlaps);
    }

    /// <param name="monitors">同一台电脑的全部屏幕。</param>
    /// <param name="seedId">从哪块屏幕开始寻找相邻屏幕。</param>
    private static HashSet<string> ConnectedIds(IReadOnlyList<SharedMonitor> monitors, string seedId)
    {
        var connected = new HashSet<string>();
        var queue = new Queue<SharedMonitor>();
        var seed = monitors.FirstOrDefault(m => m.Id == seedId);
        if (seed == null) return connected;
        connected.Add(seed.Id);
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var monitor in monitors)
                if (!connected.Contains(monitor.Id) && Rect(current).Touches(Rect(monitor)))
                {
                    connected.Add(monitor.Id);
                    queue.Enqueue(monitor);
                }
        }
        return connected;
    }

    /// <param name="moving">需要整体移动的一组屏幕。</param>
    /// <param name="baseItems">保持不动、用于吸附的一组屏幕。</param>
    /// <param name="occupied">不允许重叠的全部屏幕。</param>
    private static (int Dx, int Dy)? FindCompactShift(IReadOnlyList<SharedMonitor> moving,
        IReadOnlyList<SharedMonitor> baseItems, IReadOnlyList<SharedMonitor> occupied)
    {
        (int Dx, int Dy)? best = null;
        var bestDistance = int.MaxValue;
        foreach (var screen in moving)
        {
            foreach (var anchor in baseItems)
            {
                if (Math.Min(screen.Y + screen.CanvasHeight, anchor.Y + anchor.CanvasHeight) > Math.Max(screen.Y, anchor.Y))
                {
                    Consider(anchor.X + anchor.CanvasWidth - screen.X, 0);
                    Consider(anchor.X - screen.X - screen.CanvasWidth, 0);
                }
                if (Math.Min(screen.X + screen.CanvasWidth, anchor.X + anchor.CanvasWidth) > Math.Max(screen.X, anchor.X))
                {
                    Consider(0, anchor.Y + anchor.CanvasHeight - screen.Y);
                    Consider(0, anchor.Y - screen.Y - screen.CanvasHeight);
                }
            }
        }
        return best;

        void Consider(int dx, int dy)
        {
            var distance = Math.Abs(dx) + Math.Abs(dy);
            if (distance == 0 || distance >= bestDistance) return;
            var moved = moving.Select(m => new SharedLayoutRect(m.X + dx, m.Y + dy, m.CanvasWidth, m.CanvasHeight)).ToList();
            if (moved.Any(m => occupied.Any(o => m.Overlaps(Rect(o))))) return;
            if (!moved.Any(m => baseItems.Any(b => m.Touches(Rect(b))))) return;
            best = (dx, dy);
            bestDistance = distance;
        }
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
        if (hub.Engine.IsBusy) throw new InvalidOperationException("副屏正在切换缩放，切换完成后自动恢复布局操作");
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
            if (TryFindOverlap(desired.Monitors, out var overlapA, out var overlapB))
                throw new InvalidOperationException($"显示器不能重叠：{overlapA!.Owner} · {overlapA.Name} / {overlapB!.Owner} · {overlapB.Name}");
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
