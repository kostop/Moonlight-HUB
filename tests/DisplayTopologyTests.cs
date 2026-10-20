using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class DisplayTopologyTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("虚拟屏与物理屏共用 source 被识别为复制模式", () =>
        {
            var panel = Physical();
            var virtualDisplay = Virtual();
            virtualDisplay.GdiName = panel.GdiName;
            Program.Assert(DisplayManager.SharesPhysicalSource([panel, virtualDisplay]), "复制模式没有被识别");
        }));

        tests.Add(("独立扩展屏不会被误判为复制模式", () =>
        {
            Program.Assert(!DisplayManager.SharesPhysicalSource([Physical(), Virtual()]), "独立扩展屏被误判");
        }));

        tests.Add(("待机虚拟屏不参与复制模式判断", () =>
        {
            var panel = Physical();
            var virtualDisplay = Virtual();
            virtualDisplay.GdiName = panel.GdiName;
            virtualDisplay.Active = false;
            Program.Assert(!DisplayManager.SharesPhysicalSource([panel, virtualDisplay]), "待机虚拟屏被误判为复制模式");
        }));
    }

    private static DisplayInfo Physical() => new()
    {
        GdiName = @"\\.\DISPLAY1",
        DevicePath = @"\\?\DISPLAY#HKC2710#TEST&UID4352#PHYSICAL",
        Active = true,
        Primary = true
    };

    private static DisplayInfo Virtual() => new()
    {
        GdiName = @"\\.\DISPLAY5",
        DevicePath = @"\\?\DISPLAY#PSCCDD0#TEST&UID256#VIRTUAL",
        Active = true
    };
}
