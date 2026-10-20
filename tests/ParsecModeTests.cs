using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class ParsecModeTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("其它分辨率的注册模式也进入可选列表", () =>
        {
            var choices = ParsecModeCatalog.Merge(
                [new DisplayMode(2736, 1260, 120, 0), new DisplayMode(2000, 1200, 60, 0)],
                [new ParsecCustomMode(1, 2000, 1200, 120)],
                2736, 1260, 120);
            Program.Assert(choices.Any(c => c.Width == 2000 && c.Height == 1200 && c.Hz == 120 && !c.Exposed),
                "注册表中的 2000×1200@120 没有出现在模式列表");
            Program.Assert(choices.Any(c => c.Width == 2000 && c.Height == 1200 && c.Hz == 60 && c.Exposed),
                "驱动已发布的 2000×1200@60 丢失");
            Program.Assert(choices[0].Width == 2736 && choices[0].Height == 1260 && choices[0].Hz == 120,
                "方案当前模式没有排在首位");
        }));

        tests.Add(("已发布的重复模式不会被标成待重新插拔", () =>
        {
            var choices = ParsecModeCatalog.Merge(
                [new DisplayMode(1920, 1080, 60, 0)],
                [new ParsecCustomMode(0, 1920, 1080, 60)],
                1920, 1080, 60);
            Program.Assert(choices.Count(c => c.Width == 1920 && c.Height == 1080 && c.Hz == 60) == 1,
                "同一模式出现了多次");
            Program.Assert(choices.Single().Exposed, "已发布的模式被误标为待重新插拔");
        }));

        tests.Add(("方案当前模式未被枚举时仍可显示", () =>
        {
            var choices = ParsecModeCatalog.Merge(
                [new DisplayMode(1920, 1080, 60, 0)],
                [],
                2000, 1200, 60);
            Program.Assert(choices[0].Width == 2000 && choices[0].Height == 1200 && choices[0].Hz == 60,
                "方案当前模式缺失时没有补进列表");
        }));

        tests.Add(("待机虚拟屏的模式列表不显示重新插拔提示", () =>
        {
            var choices = ParsecModeCatalog.Merge(
                [],
                [new ParsecCustomMode(1, 2000, 1200, 120)],
                2000, 1200, 120,
                missingNote: string.Empty);
            Program.Assert(choices.Single().ToString() == "2000×1200 @ 120Hz",
                "待机模式被误标为重新插拔后可用");
        }));
    }
}
