namespace MoonlightHub.Core;

/// <summary>短暂断开期间保留完整的物理屏集合，不能用 Windows 的临时子集覆盖布局记忆。</summary>
public sealed class DisplayReconnectState
{
    private readonly object _gate = new();
    private HashSet<string> _expected;
    private HashSet<string> _observed;
    private bool _pending;

    /// <param name="expectedIds">最近一次完整方案包含的物理屏稳定 ID。</param>
    public DisplayReconnectState(IEnumerable<string> expectedIds)
    {
        _expected = Ids(expectedIds);
        _observed = new(_expected, StringComparer.OrdinalIgnoreCase);
    }

    public bool Pending { get { lock (_gate) return _pending; } }
    public IReadOnlyList<string> ExpectedIds { get { lock (_gate) return _expected.ToList(); } }

    /// <param name="expectedIds">用户明确重新应用方案后确认的物理屏集合。</param>
    public void Reset(IEnumerable<string> expectedIds)
    {
        lock (_gate)
        {
            _expected = Ids(expectedIds);
            _observed = new(_expected, StringComparer.OrdinalIgnoreCase);
            _pending = false;
        }
    }

    /// <param name="displays">当前显示器快照，待机虚拟屏不参与物理连接判断。</param>
    public bool Observe(IReadOnlyList<DisplayInfo> displays)
    {
        var current = Ids(displays.Where(d => d.IsReady && !d.IsParsec).Select(d => d.SunshineDeviceId));
        lock (_gate)
        {
            var changed = !_observed.SetEquals(current);
            _observed = current;
            if (_pending) return changed;
            if (_expected.Count > 0 && !_expected.IsSubsetOf(current))
            {
                _pending = true;
                return true;
            }
            _expected = current;
            return false;
        }
    }

    /// <param name="displays">恢复成功后实际激活的显示器快照。</param>
    public bool Complete(IReadOnlyList<DisplayInfo> displays)
    {
        var current = Ids(displays.Where(d => d.IsReady && !d.IsParsec).Select(d => d.SunshineDeviceId));
        lock (_gate)
        {
            if (!_expected.IsSubsetOf(current)) return false;
            _expected = current;
            _observed = new(current, StringComparer.OrdinalIgnoreCase);
            _pending = false;
            return true;
        }
    }

    private static HashSet<string> Ids(IEnumerable<string> ids)
        => ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record DisplayReconnectPlan(bool Ready, bool Changed, List<LayoutItem> Items, string Reason);

/// <summary>纯布局策略：恢复坐标及主屏，保持当前分辨率、刷新率、旋转和待机状态。</summary>
public static class DisplayReconnectLayout
{
    /// <param name="previousIds">已持久保存的完整物理屏目标。</param>
    /// <param name="currentIds">这次方案应用后实际激活的物理屏 ID。</param>
    /// <param name="automatic">自动或失败的应用只能补充目标，不能确认删除缺失屏。</param>
    public static List<string> ExpectedIdsAfterApply(IEnumerable<string> previousIds, IEnumerable<string> currentIds, bool automatic)
        => (automatic ? previousIds.Concat(currentIds) : currentIds).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <param name="displays">当前已枚举的显示器，布局项始终绑定当前设备路径。</param>
    /// <param name="expectedIds">热插拔前的完整物理屏集合，必须全部回归才能恢复。</param>
    /// <param name="physicalLayout">用户确认过的物理屏坐标及主屏。</param>
    /// <param name="virtualDisplays">当前方案中的虚拟屏位置记忆。</param>
    public static DisplayReconnectPlan Build(IReadOnlyList<DisplayInfo> displays, IReadOnlyCollection<string> expectedIds,
        IReadOnlyDictionary<string, DisplayPlacement> physicalLayout, IReadOnlyList<VirtualDisplaySpec> virtualDisplays)
    {
        var active = displays.Where(d => d.IsReady).ToList();
        var physical = active.Where(d => !d.IsParsec).ToList();
        var ids = physical.Select(d => d.SunshineDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expectedIds.Count == 0 || expectedIds.Any(id => !ids.Contains(id)))
            return new(false, false, [], "等待原有物理显示器重新连接");
        var remembered = physicalLayout.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var primary = physical.Where(d => remembered.TryGetValue(d.SunshineDeviceId, out var old) && old.Primary).ToList();
        if (primary.Count != 1) return new(false, false, [], "没有唯一的主屏位置记忆，保留原布局等待确认");
        if (DisplayManager.SharesPhysicalSource(active)) return new(false, false, [], "显示器仍在重新枚举或处于复制模式");
        var origin = remembered[primary[0].SunshineDeviceId];
        var items = new List<LayoutItem>();
        foreach (var display in active)
        {
            var x = display.PositionX;
            var y = display.PositionY;
            if (!display.IsParsec && remembered.TryGetValue(display.SunshineDeviceId, out var old))
                (x, y) = (old.X - origin.X, old.Y - origin.Y);
            else if (display.IsParsec && virtualDisplays.FirstOrDefault(v => v.Slot == display.ParsecSlot) is { Placement: Placement.Custom, X: { } vx, Y: { } vy })
                (x, y) = (vx - origin.X, vy - origin.Y);
            // 明确保留当前刷新率，避免通用摆放逻辑顺便修正虚拟屏的刷新率。
            items.Add(new LayoutItem(display) { X = x, Y = y, Primary = display == primary[0], RefreshRate = display.RefreshRate });
        }
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (items[i].X < items[j].X + active[j].Width && items[j].X < items[i].X + active[i].Width &&
                    items[i].Y < items[j].Y + active[j].Height && items[j].Y < items[i].Y + active[i].Height)
                    return new(false, false, [], "当前屏幕尺寸与记忆位置冲突，暂停自动恢复");
        var changed = items.Where((item, index) => item.X != active[index].PositionX || item.Y != active[index].PositionY || item.Primary != active[index].Primary).Any();
        return new(true, changed, items, string.Empty);
    }
}
