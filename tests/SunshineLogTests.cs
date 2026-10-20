using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class SunshineLogTests
{
    public static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("Sunshine 日志最后事件为断开时返回未连接", () =>
        {
            const string log = "CLIENT CONNECTED\n...\nCLIENT DISCONNECTED\n";
            Program.Assert(SunshineInstanceManager.ParseLastClientConnection(log) == false, "最后断开事件未被识别");
        }));

        tests.Add(("Sunshine 日志最后事件为连接时返回已连接", () =>
        {
            const string log = "CLIENT DISCONNECTED\n...\nCLIENT CONNECTED\n";
            Program.Assert(SunshineInstanceManager.ParseLastClientConnection(log) == true, "最后连接事件未被识别");
        }));

        tests.Add(("Sunshine 日志没有连接事件时返回未知", () =>
        {
            Program.Assert(SunshineInstanceManager.ParseLastClientConnection("no client events") == null, "无事件时不应猜测状态");
        }));
    }
}
