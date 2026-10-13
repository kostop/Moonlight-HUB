using MoonlightHub.Core;

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
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("共享布局不符合预期"); }
}
