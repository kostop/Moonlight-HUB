using MoonlightHub.Core;
using System.Text.Json;

namespace MoonlightHub.Tests;

internal static class DisplayReconnectTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("缺屏自动修复后重启仍等待完整物理集合并可恢复", () =>
        {
            var saved = new HubState { LastAppliedActiveIds = ["panel", "external"] };
            saved.LastAppliedActiveIds = DisplayReconnectLayout.ExpectedIdsAfterApply(saved.LastAppliedActiveIds, ["panel"], automatic: true);
            var restarted = JsonSerializer.Deserialize<HubState>(JsonSerializer.Serialize(saved))!;
            var state = new DisplayReconnectState(restarted.LastAppliedActiveIds);
            state.Observe([Panel()]);
            Program.Assert(state.Pending && state.ExpectedIds.Contains("external"), "重启后丢失了待回归的外屏");
            state.Observe([Panel(), External()]);
            var plan = Plan([Panel(), External()], state.ExpectedIds);
            Program.Assert(plan.Ready && state.Complete([Panel(), External()]), "外屏回归后无法完成恢复");
        }));
        tests.Add(("用户明确接受物理子集后持久目标允许缩减", () =>
        {
            var ids = DisplayReconnectLayout.ExpectedIdsAfterApply(["panel", "external"], ["panel"], automatic: false);
            var state = new DisplayReconnectState(["panel", "external"]);
            state.Observe([Panel()]);
            state.Reset(ids);
            var restarted = new DisplayReconnectState(JsonSerializer.Deserialize<List<string>>(JsonSerializer.Serialize(ids))!);
            Program.Assert(!state.Pending && !restarted.Observe([Panel()]) && !restarted.Pending, "明确接受子集后仍等待已删除屏");
        }));
        tests.Add(("自动应用补充新屏且忽略稳定ID大小写重复", () =>
        {
            var ids = DisplayReconnectLayout.ExpectedIdsAfterApply(["PANEL", "external"], ["panel", "new"], automatic: true);
            Program.Assert(ids.Count == 3 && ids.Contains("external") && ids.Contains("new"), "自动应用丢失历史屏或重复记忆同屏");
        }));
        tests.Add(("用户明确重新应用后允许替换旧重连目标", () =>
        {
            var state = new DisplayReconnectState(["panel", "external"]);
            state.Observe([Panel()]);
            state.Reset(["panel"]);
            Program.Assert(!state.Pending && state.ExpectedIds.Count == 1 && !state.Observe([Panel()]), "显式重新应用仍被旧缺屏目标阻塞");
        }));
        tests.Add(("热插拔子集不会覆盖原物理屏全集", () =>
        {
            var state = new DisplayReconnectState(["panel", "external"]);
            Program.Assert(state.Observe([Panel()]) && state.Pending, "断开未进入恢复保护");
            state.Observe([Panel()]);
            Program.Assert(state.ExpectedIds.Count == 2 && state.ExpectedIds.Contains("external"), "子集覆盖了完整目标");
            Program.Assert(!state.Complete([Panel()]) && state.Pending, "屏幕尚缺失就结束保护");
        }));
        tests.Add(("原屏回归后重新等待稳定且成功恢复才解除保护", () =>
        {
            var state = new DisplayReconnectState(["panel", "external"]);
            state.Observe([Panel()]);
            Program.Assert(state.Observe([Panel(), External()]), "未报告原屏回归的连接变化");
            Program.Assert(state.Pending && state.ExpectedIds.Count == 2, "回归时过早解除保护");
            Program.Assert(state.Complete([Panel(), External()]) && !state.Pending, "成功恢复后未解除保护");
        }));
        tests.Add(("相同物理集合手动拖屏不触发恢复", () =>
        {
            var state = new DisplayReconnectState(["panel", "external"]);
            var panel = Panel(); panel.PositionY = 1600;
            var external = External(); external.Primary = true;
            Program.Assert(!state.Observe([panel, external]) && !state.Pending, "手动拖屏被当作热插拔");
        }));
        tests.Add(("待机虚拟屏变化不触发物理重连保护", () =>
        {
            var state = new DisplayReconnectState(["panel", "external"]);
            state.Observe([Panel(), External(), Virtual()]);
            Program.Assert(!state.Observe([Panel(), External()]) && !state.Pending, "虚拟屏待机触发了物理恢复");
        }));
        tests.Add(("稳定ID大小写及GDI重编号不改变连接集合", () =>
        {
            var state = new DisplayReconnectState(["PANEL", "EXTERNAL"]);
            var panel = Panel(); panel.GdiName = @"\\.\DISPLAY8";
            Program.Assert(!state.Observe([panel, External()]) && !state.Pending, "显示编号变化被当作物理断开");
        }));
        tests.Add(("重连恢复只改坐标和原主屏并保持串流模式", () =>
        {
            var panel = Panel(); panel.Primary = false; panel.PositionX = 2560;
            var external = External(); external.Primary = true; external.PositionX = 0;
            var virtualDisplay = Virtual(); virtualDisplay.PositionX = -2000;
            var plan = Plan([panel, external, virtualDisplay]);
            Program.Assert(plan.Ready && plan.Changed && plan.Items.Count == 3, "未构建恢复计划");
            Program.Assert(plan.Items[0].Primary && !plan.Items[1].Primary && plan.Items[1].X == 1920, "未恢复原主屏和外屏位置");
            Program.Assert(plan.Items[2].X == 4480 && plan.Items[2].Y == 0, "未恢复虚拟屏位置");
            Program.Assert(plan.Items.All(i => i.Width == null && i.Height == null && i.Orientation == null && i.Attached), "位置恢复修改了模式或激活状态");
            Program.Assert(plan.Items[0].RefreshRate == panel.RefreshRate && plan.Items[1].RefreshRate == external.RefreshRate && plan.Items[2].RefreshRate == virtualDisplay.RefreshRate, "位置恢复未保持当前刷新率");
            Program.Assert(plan.Items[1].ExpectedDevicePath == external.DevicePath, "恢复项未绑定当前枚举身份");
        }));
        tests.Add(("物理屏未全部回归时不应用部分恢复", () =>
        {
            var plan = Plan([Panel(), Virtual()]);
            Program.Assert(!plan.Ready && plan.Items.Count == 0, "物理屏子集被应用为完整布局");
        }));
        tests.Add(("恢复不激活待机虚拟屏且已正确位置不重复应用", () =>
        {
            var virtualDisplay = Virtual(); virtualDisplay.Active = false;
            var plan = Plan([Panel(), External(), virtualDisplay]);
            Program.Assert(plan.Ready && !plan.Changed && plan.Items.Count == 2, "恢复启用了待机屏或重复重排");
        }));
        tests.Add(("尺寸变化产生重叠时暂停重连恢复", () =>
        {
            var panel = Panel(); panel.Width = 2400;
            var plan = Plan([panel, External()]);
            Program.Assert(!plan.Ready && plan.Items.Count == 0, "尺寸冲突仍应用旧坐标");
        }));
        tests.Add(("复制模式暂态不会被恢复为独立扩展布局", () =>
        {
            var panel = Panel(); var virtualDisplay = Virtual(); virtualDisplay.GdiName = panel.GdiName;
            var plan = Plan([panel, External(), virtualDisplay]);
            Program.Assert(!plan.Ready && plan.Items.Count == 0, "复制模式暂态仍执行恢复");
        }));
    }

    private static DisplayReconnectPlan Plan(List<DisplayInfo> displays, IReadOnlyCollection<string>? expectedIds = null)
        => DisplayReconnectLayout.Build(displays, expectedIds ?? ["panel", "external"], new Dictionary<string, DisplayPlacement>
        {
            ["panel"] = new() { X = 0, Y = 0, Width = 1920, Height = 1080, Primary = true },
            ["external"] = new() { X = 1920, Y = 0, Width = 2560, Height = 1440 }
        }, [new() { Slot = 0, Placement = Placement.Custom, X = 4480, Y = 0 }]);

    private static DisplayInfo Panel() => new()
    {
        GdiName = @"\\.\DISPLAY1", DevicePath = "panel-path", SunshineDeviceId = "panel",
        Active = true, Primary = true, Width = 1920, Height = 1080, RefreshRate = 60
    };

    private static DisplayInfo External() => new()
    {
        GdiName = @"\\.\DISPLAY2", DevicePath = "external-path", SunshineDeviceId = "external",
        Active = true, Width = 2560, Height = 1440, PositionX = 1920, RefreshRate = 120
    };

    private static DisplayInfo Virtual() => new()
    {
        GdiName = @"\\.\DISPLAY3", DevicePath = @"\\?\DISPLAY#PSCCDD0#TEST&UID256#VIRTUAL", SunshineDeviceId = "virtual",
        Active = true, Width = 2000, Height = 1200, PositionX = 4480, RefreshRate = 120
    };
}
