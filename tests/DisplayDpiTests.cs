using MoonlightHub.Core;
using System.Text.Json;

namespace MoonlightHub.Tests;

internal static class DisplayDpiTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("DPI 比例不会改变 iPhone 原生分辨率", () =>
        {
            var profile = new Profile { AutoMatchClientResolution = false,
                VirtualDisplays = [new() { Width = 2736, Height = 1260 }] };
            var settings = new HubSettings { Profiles = [profile] };
            using var vdd = new ParsecVdd();
            var engine = new ProfileEngine(settings, new HubState(), vdd, new SunshineInstanceManager(settings));
            foreach (var percent in new[] { 100, 125, 150, 175, 200, 250, 500 })
            {
                profile.VirtualDisplays[0].WindowsScalePercent = percent;
                Program.Assert(engine.TargetResolution(profile, profile.VirtualDisplays[0], null) == (2736, 1260), "真实 DPI 设置改变了像素尺寸");
            }
        }));
        tests.Add(("旧降分辨率缩放设置不再影响渲染尺寸", () =>
        {
            var profile = new Profile { AutoMatchClientResolution = false,
                VirtualDisplays = [new() { Width = 2736, Height = 1260, ContentScalePercent = 250 }] };
            var settings = new HubSettings { Profiles = [profile] };
            using var vdd = new ParsecVdd();
            var engine = new ProfileEngine(settings, new HubState(), vdd, new SunshineInstanceManager(settings));
            Program.Assert(engine.TargetResolution(profile, profile.VirtualDisplays[0], null) == (2736, 1260), "旧设置仍降低了分辨率");
        }));
        tests.Add(("两块副屏的真实 DPI 独立保存", () =>
        {
            var profile = new Profile { VirtualDisplays = [new() { WindowsScalePercent = 150 }, new() { WindowsScalePercent = 200 }] };
            var clone = profile.Clone(); clone.VirtualDisplays[0].WindowsScalePercent = 175;
            var restored = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
            Program.Assert(profile.VirtualDisplays[0].WindowsScalePercent == 150 && restored.VirtualDisplays[1].WindowsScalePercent == 200, "副屏 DPI 未独立保存");
        }));
        tests.Add(("布局按真实 DPI 缩放且保留原生像素", () =>
        {
            var display = new SharedMonitor("qingxian", "phone", "虚拟屏", 0, 0, 0, 0, 2736, 1260, false, true, true, 200);
            var scaled = display with { WindowsScalePercent = 250 };
            Program.Assert(display.CanvasWidth == 1368 && scaled.CanvasWidth == 1094 && scaled.CanvasHeight == 504, "逻辑尺寸未跟随实际 DPI");
            Program.Assert(scaled.Width == 2736 && scaled.Height == 1260, "布局缩放修改了原生像素");
        }));
    }
}
