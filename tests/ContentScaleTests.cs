using System.Text.Json;
using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class ContentScaleTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("旧方案默认原始画面大小", () =>
        {
            var spec = JsonSerializer.Deserialize<VirtualDisplaySpec>("{\"Width\":2736,\"Height\":1260}")!;
            Check(spec.ContentScalePercent == 100 && VirtualContentScale.RenderSize(2736, 1260, 100) == (2736, 1260));
        }));
        tests.Add(("iPhone 额外放大 150% 保持输出尺寸", () =>
        {
            var spec = new VirtualDisplaySpec { Width = 2736, Height = 1260, ContentScalePercent = 150 };
            Check(VirtualContentScale.RenderSize(spec.Width, spec.Height, spec.ContentScalePercent) == (1824, 840));
            Check(spec.Width == 2736 && spec.Height == 1260);
        }));
        tests.Add(("支持 137% 并保持长宽比和偶数像素", () =>
        {
            var (w, h) = VirtualContentScale.RenderSize(2736, 1260, 137);
            Check(w % 2 == 0 && h % 2 == 0 && Math.Abs((double)w / h - 2736.0 / 1260) < .005);
            Check(Math.Abs(2736.0 / w - 1.37) < .005);
        }));
        tests.Add(("50% 显示更多内容", () => Check(VirtualContentScale.RenderSize(2736, 1260, 50) == (5472, 2520))));
        tests.Add(("每块副屏独立保存和复制", () =>
        {
            var p = new Profile { VirtualDisplays = [new() { ContentScalePercent = 150 }, new() { ContentScalePercent = 137 }] };
            var clone = p.Clone(); clone.VirtualDisplays[0].ContentScalePercent = 200;
            var roundtrip = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p))!;
            Check(p.VirtualDisplays[0].ContentScalePercent == 150 && roundtrip.VirtualDisplays[1].ContentScalePercent == 137);
        }));
        tests.Add(("无效比例和超大渲染尺寸被拒绝", () =>
        {
            foreach (var percent in new[] { 0, 49, 301 }) Rejected(() => VirtualContentScale.RenderSize(2736, 1260, percent));
            Rejected(() => VirtualContentScale.RenderSize(7680, 4320, 50));
            Rejected(() => VirtualContentScale.RenderSize(640, 480, 300));
        }));
    }

    private static void Check(bool value) { if (!value) throw new Exception("画面缩放不符合预期"); }
    private static void Rejected(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("没有拒绝不支持的画面大小");
    }
}
