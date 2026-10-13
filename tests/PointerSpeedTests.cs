using System.Reflection;
using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class PointerSpeedTests
{
    private static readonly MethodInfo FollowLogMethod = typeof(PointerSpeedManager).GetMethod(
        "FollowDeskflowLog", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Deskflow 鼠标所在屏幕跟踪方法");

    /// <param name="tests">接收不调用系统鼠标接口的日志状态回归用例。</param>
    internal static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("正常跨机后使用远端速度", () => Expect(null, "switch from \"Desktop-Lin\" to \"qingxian\" at 50,10", "qingxian")));
        tests.Add(("远端断线强制跳回后恢复本机屏幕速度", () => Expect("qingxian", "INFO: jump from \"qingxian\" to \"Desktop-Lin\" at 1000,600", null)));
        tests.Add(("远端断线通知清除残留速度", () => Expect("qingxian", "IPC: client \"qingxian\" has disconnected", null)));
        tests.Add(("远端心跳超时清除残留速度", () => Expect("qingxian", "IPC: client \"qingxian\" is dead", null)));
        tests.Add(("主动断开远端清除残留速度", () => Expect("qingxian", "NOTE: disconnecting client \"qingxian\"", null)));
        tests.Add(("强制断开远端清除残留速度", () => Expect("qingxian", "NOTE: forced disconnection of client \"qingxian\"", null)));
        tests.Add(("无关客户端断线不会清除正在控制的远端", () => Expect("qingxian", "IPC: client \"other-pc\" has disconnected", "qingxian")));
        tests.Add(("服务端重启后同批次的新切换仍按日志顺序生效", () => Expect("old-peer", "started server, waiting for clients\nswitch from \"Desktop-Lin\" to \"qingxian\" at 50,10\n", "qingxian")));
        tests.Add(("切换后服务端停止恢复本机速度", () => Expect(null, "switch from \"Desktop-Lin\" to \"qingxian\" at 50,10\nstopped server\n", null)));
        tests.Add(("同批次断线重连后使用新的远端速度", () => Expect("qingxian", "client \"qingxian\" has disconnected\nswitch from \"Desktop-Lin\" to \"qingxian\" at 50,10\n", "qingxian")));
        tests.Add(("强制跳回忽略本机名称大小写", () => Expect("qingxian", "jump from \"qingxian\" to \"desktop-lin\" at 1000,600", null)));
    }

    /// <param name="before">处理日志前正在控制的远端屏幕。</param>
    /// <param name="log">完整日志片段。</param>
    /// <param name="expected">处理后预期的远端屏幕；null 表示回到本机。</param>
    private static void Expect(string? before, string log, string? expected)
    {
        var actual = (string?)FollowLogMethod.Invoke(null, [before, "Desktop-Lin", log]);
        Program.Assert(actual == expected, $"预期屏幕 {expected ?? "本机"}，实际 {actual ?? "本机"}");
    }
}
