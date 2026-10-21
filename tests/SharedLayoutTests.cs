using MoonlightHub.Core;
using System.Text.Json;

namespace MoonlightHub.Tests;

internal static class SharedLayoutTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("200% 显示器按逻辑尺寸绘制", () =>
        {
            var phone = new SharedMonitor("qingxian", "phone", "虚拟屏", 1920, 0, 1920, 0, 2736, 1260, false, true, true, 200);
            Check(phone.CanvasWidth == 1368 && phone.CanvasHeight == 630);
            var enlarged = phone with { Width = 1824, Height = 840, ContentScalePercent = 150 };
            Check(enlarged.CanvasWidth == 912 && enlarged.CanvasHeight == 420);
        }));
        tests.Add(("缩放不同的屏幕边缘映射一致", () =>
        {
            Check(SharedDisplayLayout.ProjectAxis(1920, 1920, 912, 1920, 1824) == 1920);
            Check(SharedDisplayLayout.ProjectAxis(-912, 1920, 912, 1920, 1824) == -1824);
            Check(SharedDisplayLayout.ProjectAxis(660, 1080, 420, 1080, 840) == 240);
            Check(SharedDisplayLayout.ProjectAxis(-420, 1080, 420, 1080, 840) == -840);
        }));
        tests.Add(("待生效缩放预览保留真实鼠标坐标", () =>
        {
            var pending = new SharedMonitor("qingxian", "phone", "虚拟屏", 1920, 0, 1920, 0,
                2736, 1260, false, true, true, 200, 150, true, 0, true, 1824, 840);
            Check(pending.CanvasWidth == 912 && pending.CanvasHeight == 420);
            Check(pending.Width == 2736 && pending.Height == 1260 && pending.NativeX == 1920);
            var applied = pending with { ScalePending = false, Width = 1824, Height = 840 };
            Check(applied.CanvasWidth == pending.CanvasWidth && applied.CanvasHeight == pending.CanvasHeight);
        }));
        tests.Add(("两块屏幕分别保留而不是合并长条", () =>
        {
            var p = new SharedMonitor("qingxian", "real", "真实屏", 0, 0, 0, 0, 1920, 1080, true, false, true);
            var v = new SharedMonitor("qingxian", "phone", "虚拟屏", 0, 0, 1920, 0, 1824, 840, false, true, true, 200, 150);
            var doc = new SharedDisplayDocument();
            SharedDisplayLayout.Merge(doc, [p, v], 200, -1200);
            Check(doc.Monitors.Count == 2 && doc.Monitors[1].X == 2120 && doc.Monitors[1].CanvasWidth == 912);
        }));
        tests.Add(("逻辑间隙和主机位置不会被物理坐标覆盖", () =>
        {
            var p = new SharedMonitor("qingxian", "real", "真实屏", 500, -1400, 0, 0, 1920, 1080, true, false, true);
            var v = new SharedMonitor("qingxian", "phone", "虚拟屏", 2800, -1400, 1920, 0, 1824, 840, false, true, true, 200, 150);
            var doc = new SharedDisplayDocument { Monitors = [p, v] };
            Check(!SharedDisplayLayout.Merge(doc, [p with { X = 0, Y = 0 }, v with { X = 1920, Y = 0 }], 0, 0));
            Check(doc.Monitors[1].X == 2800);
        }));
        tests.Add(("新增屏幕放到本机右下角且不移动已有屏", () =>
        {
            var p = new SharedMonitor("pc", "main", "主屏", 0, 0, 0, 0, 1920, 1080, true, false, true);
            var s = new SharedMonitor("pc", "second", "副屏", 1920, 0, 1920, 0, 1280, 720, false, false, true);
            var n = new SharedMonitor("pc", "new", "新屏", 3000, 500, 3000, 500, 1600, 900, false, false, true);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            Check(SharedDisplayLayout.Merge(doc, [p, s, n], 0, 0));
            Check(doc.Monitors[0].X == 0 && doc.Monitors[1].X == 1920);
            Check(doc.Monitors[2].X == 3200 && doc.Monitors[2].Y == 180);
            Check(!SharedDisplayLayout.TryFindOverlap(doc.Monitors, out _, out _));
        }));
        tests.Add(("屏幕暂时断开不收拢其它屏且重连恢复原位", () =>
        {
            var p = new SharedMonitor("pc", "main", "主屏", 0, 0, 0, 0, 1000, 800, true, false, true);
            var middle = new SharedMonitor("pc", "middle", "中间屏", 1000, 0, 1000, 0, 1000, 800, false, false, true);
            var right = new SharedMonitor("pc", "right", "右屏", 2000, 0, 2000, 0, 1000, 800, false, false, true);
            var doc = new SharedDisplayDocument { Monitors = [p, middle, right] };
            Check(SharedDisplayLayout.Merge(doc, [p, right], 0, 0));
            Check(doc.Monitors.Single(m => m.Id == "right").X == 2000);
            Check(doc.RememberedMonitors.Single(m => m.Id == "middle").X == 1000);
            Check(SharedDisplayLayout.Merge(doc, [p, middle with { NativeX = -1000 }, right], 0, 0));
            Check(doc.Monitors.Single(m => m.Id == "middle").X == 1000);
            Check(doc.Monitors.Single(m => m.Id == "right").X == 2000);
        }));
        tests.Add(("历史重叠布局只移动冲突屏幕", () =>
        {
            var p = new SharedMonitor("pc", "main", "主屏", 0, 0, 0, 0, 1000, 800, true, false, true);
            var s = new SharedMonitor("pc", "second", "副屏", 0, 0, 0, 0, 1000, 600, false, false, true);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            Check(SharedDisplayLayout.Merge(doc, [p, s], 0, 0));
            Check(doc.Monitors[0].X == 0 && doc.Monitors[0].Y == 0);
            Check(doc.Monitors[1].X == 1000);
            Check(!SharedDisplayLayout.TryFindOverlap(doc.Monitors, out _, out _));
        }));
        tests.Add(("Windows 热插拔重排坐标不覆盖 Hub 摆放", () =>
        {
            var p = Screen("main", 300, -900, primary: true);
            var s = Screen("second", 1450, -700);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            Check(SharedDisplayLayout.Merge(doc, [p with { NativeX = 1600, NativeY = 800 }, s with { NativeX = 0, NativeY = 0 }], 0, 0));
            Position(doc, "main", 300, -900);
            Position(doc, "second", 1450, -700);
        }));
        tests.Add(("主屏暂时切换不会平移整台电脑", () =>
        {
            var p = Screen("main", -1200, 400, primary: true);
            var s = Screen("second", 200, 600);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [s with { Primary = true, NativeX = 0, NativeY = 0 }], 0, 0);
            Position(doc, "second", 200, 600);
            SharedDisplayLayout.Merge(doc, [s with { Primary = false }, p], 0, 0);
            Position(doc, "main", -1200, 400);
            Position(doc, "second", 200, 600);
        }));
        tests.Add(("离线屏位置经保存重启和克隆后仍可恢复", () =>
        {
            var p = Screen("main", 250, 200, primary: true);
            var s = Screen("second", 1500, -100);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [p], 0, 0);
            var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            doc = JsonSerializer.Deserialize<SharedDisplayDocument>(JsonSerializer.Serialize(doc, json), json)!;
            doc = SharedDisplayLayout.Clone(doc);
            SharedDisplayLayout.Merge(doc, [p, s with { NativeX = 0, NativeY = 800 }], 0, 0);
            Position(doc, "second", 1500, -100);
            Check(doc.RememberedMonitors.Count == 2);
        }));
        tests.Add(("重连后的重复快照不会漂移", () =>
        {
            var p = Screen("main", -1500, -800, primary: true);
            var s = Screen("second", -300, -600);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [p], 0, 0);
            var live = new[] { s with { NativeX = 0, NativeY = 800 }, p with { NativeX = 1000, NativeY = 0 } };
            SharedDisplayLayout.Merge(doc, live, 0, 0);
            for (var i = 0; i < 5; i++) Check(!SharedDisplayLayout.Merge(doc, live, 0, 0));
            Position(doc, "main", -1500, -800);
            Position(doc, "second", -300, -600);
        }));
        tests.Add(("临时 DPI 放大造成避让后恢复原位且重复快照不漂移", () =>
        {
            var p = Screen("main", 0, 0, primary: true) with { Width = 2000, Height = 1600, WindowsScalePercent = 200 };
            var s = Screen("second", 1000, 0) with { NativeX = 2000 };
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            var transient = new[] { p with { WindowsScalePercent = 100 }, s };
            Check(SharedDisplayLayout.Merge(doc, transient, 0, 0));
            Position(doc, "main", 0, 0);
            Position(doc, "second", 2000, 800);
            Check(doc.RememberedMonitors.Single(m => m.Id == "second").X == 1000);
            for (var i = 0; i < 5; i++) Check(!SharedDisplayLayout.Merge(doc, transient, 0, 0));
            Check(SharedDisplayLayout.Merge(doc, [p, s], 0, 0));
            Position(doc, "second", 1000, 0);
            for (var i = 0; i < 5; i++) Check(!SharedDisplayLayout.Merge(doc, [p, s], 0, 0));
            Check(!SharedDisplayLayout.TryFindOverlap(doc.Monitors, out _, out _));
        }));
        tests.Add(("临时避让期间明确应用新摆放后不再跳回旧位置", () =>
        {
            var p = Screen("main", 0, 0, primary: true) with { Width = 2000, Height = 1600, WindowsScalePercent = 200 };
            var s = Screen("second", 1000, 0) with { NativeX = 2000 };
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [p with { WindowsScalePercent = 100 }, s], 0, 0);
            SharedDisplayLayout.RememberPositions(doc, doc.Monitors, overwrite: true);
            SharedDisplayLayout.Merge(doc, [p, s], 0, 0);
            Position(doc, "second", 2000, 800);
            Check(!SharedDisplayLayout.Merge(doc, [p, s], 0, 0));
        }));
        tests.Add(("两台电脑相同屏幕 ID 的位置记忆互不干扰", () =>
        {
            var local = Screen("same", -1500, 0, primary: true);
            var peer = local with { Owner = "qingxian", X = 800, Y = -600 };
            var doc = new SharedDisplayDocument { Monitors = [local, peer] };
            SharedDisplayLayout.Merge(doc, [local with { NativeX = 500 }], 0, 0);
            SharedDisplayLayout.Merge(doc, [peer with { NativeX = -500 }], 0, 0);
            Check(doc.Monitors.Single(m => m.Owner == "pc").X == -1500);
            Check(doc.Monitors.Single(m => m.Owner == "qingxian").X == 800);
            Check(doc.RememberedMonitors.Count == 2);
        }));
        tests.Add(("设备名称大小写变化不会被当成新屏幕", () =>
        {
            var p = Screen("main", -1200, -400, primary: true);
            var doc = new SharedDisplayDocument { Monitors = [p] };
            SharedDisplayLayout.Merge(doc, [p with { Owner = "PC", Id = "MAIN", NativeX = 500 }], 0, 0);
            Check(doc.Monitors.Count == 1 && doc.Monitors[0].X == -1200 && doc.RememberedMonitors.Count == 1);
        }));
        tests.Add(("重连位置被占用时只移动重连屏并保留原位置记忆", () =>
        {
            var p = Screen("main", 0, 0, primary: true);
            var s = Screen("second", 1200, 0);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [p], 0, 0);
            var n = Screen("new", 3000, 0);
            SharedDisplayLayout.Merge(doc, [p, n], 0, 0);
            var continuous = doc.Monitors.Single(m => m.Id == "new");
            // 使持续连接屏占住离线屏原位，模拟用户在断开期间重新摆放。
            var index = doc.Monitors.FindIndex(m => m.Id == "new");
            doc.Monitors[index] = continuous with { X = 1200, Y = 0 };
            SharedDisplayLayout.RememberPositions(doc, doc.Monitors, overwrite: true);
            SharedDisplayLayout.Merge(doc, [s, p, n], 0, 0);
            Position(doc, "main", 0, 0);
            Position(doc, "new", 1200, 0);
            Check(doc.Monitors.Single(m => m.Id == "second").X != 1200);
            Check(doc.RememberedMonitors.Single(m => m.Id == "second").X == 1200);
            Check(!SharedDisplayLayout.TryFindOverlap(doc.Monitors, out _, out _));
            for (var i = 0; i < 5; i++) Check(!SharedDisplayLayout.Merge(doc, [s, p, n], 0, 0));
            Position(doc, "new", 1200, 0);
        }));
        tests.Add(("明确应用摆放更新在线屏记忆并保留离线屏", () =>
        {
            var p = Screen("main", 0, 0, primary: true);
            var s = Screen("second", 1200, 0);
            var doc = new SharedDisplayDocument { Monitors = [p, s] };
            SharedDisplayLayout.Merge(doc, [p], 0, 0);
            doc.Monitors[0] = p with { X = -400, Y = -500 };
            SharedDisplayLayout.RememberPositions(doc, doc.Monitors, overwrite: true);
            var copy = SharedDisplayLayout.Clone(doc);
            Check(!ReferenceEquals(copy.RememberedMonitors, doc.RememberedMonitors));
            SharedDisplayLayout.Merge(copy, [p, s], 0, 0);
            Position(copy, "main", -400, -500);
            Position(copy, "second", 1200, 0);
        }));
        tests.Add(("旧布局文件可直接建立位置记忆", () =>
        {
            var doc = JsonSerializer.Deserialize<SharedDisplayDocument>("{\"Revision\":42,\"Server\":\"pc\",\"Monitors\":[]}")!;
            Check(doc.RememberedMonitors.Count == 0);
            SharedDisplayLayout.Merge(doc, [Screen("main", 0, 0, primary: true)], 400, -800);
            Check(doc.RememberedMonitors.Count == 1);
            Position(doc, "main", 400, -800);
        }));
        tests.Add(("同服务端旧响应不能覆盖新布局且首次加载不受阻", () =>
        {
            var current = new SharedDisplayDocument { Server = "pc", Revision = 200 };
            Check(!SharedDisplayLayout.CanCacheDocument(current, new() { Server = "PC", Revision = 100 }));
            Check(SharedDisplayLayout.CanCacheDocument(current, new() { Server = "pc", Revision = 200 }));
            Check(SharedDisplayLayout.CanCacheDocument(current, new() { Server = "pc", Revision = 300 }));
            Check(SharedDisplayLayout.CanCacheDocument(current, new() { Server = "qingxian", Revision = 100 }));
            Check(SharedDisplayLayout.CanCacheDocument(null, new() { Server = "pc", Revision = 100 }));
            Check(SharedDisplayLayout.CanCacheDocument(new() { Revision = 500 }, new() { Server = "pc", Revision = 100 }));
        }));
    }

    /// <param name="id">测试显示器的稳定身份。</param>
    /// <param name="x">用户保存的逻辑横坐标。</param>
    /// <param name="y">用户保存的逻辑纵坐标。</param>
    /// <param name="primary">是否为 Windows 主屏。</param>
    private static SharedMonitor Screen(string id, int x, int y, bool primary = false)
        => new("pc", id, id, x, y, 0, 0, 1000, 800, primary, false, true);

    /// <param name="document">待检查的共享布局。</param>
    /// <param name="id">显示器稳定身份。</param>
    /// <param name="x">期望的逻辑横坐标。</param>
    /// <param name="y">期望的逻辑纵坐标。</param>
    private static void Position(SharedDisplayDocument document, string id, int x, int y)
    {
        var monitor = document.Monitors.Single(m => m.Id == id);
        Check(monitor.X == x && monitor.Y == y);
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("共享布局不符合预期"); }
}
