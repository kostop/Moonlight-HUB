using MoonlightHub.Core;

namespace MoonlightHub.Tests;

internal static class DeskflowCoreGuardTests
{
    /// <summary>注册 Deskflow 核心保护的纯内存单元测试，不调用原生接口或真实进程。</summary>
    /// <param name="tests">全局测试列表。</param>
    internal static void AddTo(List<(string Name, Action Run)> tests)
    {
        tests.Add(("核心保护开启时拦截外部 deskflow off 请求并返回明确警告", BlocksExternalOffWhenProtected));
        tests.Add(("核心保护关闭时允许外部 deskflow off 请求", AllowsExternalOffWhenDisabled));
        tests.Add(("核心已处于关闭状态时不再误拦截重复 off 请求", AllowsExternalOffWhenAlreadyOff));
        tests.Add(("核心保护开启时拦截 CLI deskflow restore 请求", BlocksExternalRestoreWhenProtected));
        tests.Add(("核心保护关闭时允许 CLI deskflow restore 请求", AllowsExternalRestoreWhenDisabled));
        tests.Add(("外部篡改 deskflow.json 将角色改为关闭时被拦截并回写", BlocksExternalJsonRoleOffTamper));
        tests.Add(("外部篡改 deskflow.json 取消托管或关闭保护时被拦截并回写", BlocksExternalJsonDisableGuardAndManaged));
        tests.Add(("外部对 deskflow.json 的良性修改不被误拦", AllowsBenignExternalJsonUpdate));
        tests.Add(("运行中的核心意外消失时触发强杀告警与即时恢复", DetectsUnexpectedCoreKill));
        tests.Add(("未曾运行或期望关闭时不误报核心被强杀", IgnoresNormalOffOrColdStart));
    }

    private static void BlocksExternalOffWhenProtected()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = true };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Server, Peers = ["10.10.3.136"] };
        var decision = DeskflowCoreGuard.EvaluateExternalStop(settings, status, new DeskflowStopRequest("/api/hub/role (off)", "本机 CLI/API (127.0.0.1)", "pwsh(pid=1234)"));
        Program.Assert(!decision.Allowed, "开启核心保护时必须拦截外部关闭请求");
        Program.Assert(decision.WarningMessage.Contains("[警告]"), "警告文案缺少 [警告] 标识");
        Program.Assert(decision.WarningMessage.Contains("10.10.3.136"), "警告文案应包含已连接对端信息");
        Program.Assert(decision.WarningMessage.Contains("pwsh(pid=1234)"), "警告文案应包含调用方信息");
        Program.Assert(decision.WarningMessage.Contains("Moonlight Hub 界面"), "警告文案应提示在 Hub 界面手动操作");
    }

    private static void AllowsExternalOffWhenDisabled()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = false };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Server };
        var decision = DeskflowCoreGuard.EvaluateExternalStop(settings, status, new DeskflowStopRequest("CLI deskflow off", "本机 CLI"));
        Program.Assert(decision.Allowed, "关闭核心保护后应允许外部关闭请求");
    }

    private static void AllowsExternalOffWhenAlreadyOff()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Off, Managed = true, ProtectCoreFromExternalStop = true };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Off };
        var decision = DeskflowCoreGuard.EvaluateExternalStop(settings, status, new DeskflowStopRequest("CLI deskflow off", "本机 CLI"));
        Program.Assert(decision.Allowed, "核心已经关闭时不应对 off 返回拦截");
    }

    private static void BlocksExternalRestoreWhenProtected()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Off, Managed = true, ProtectCoreFromExternalStop = true };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Off };
        var decision = DeskflowCoreGuard.EvaluateExternalStop(settings, status, new DeskflowStopRequest("CLI deskflow restore", "本机 CLI"));
        Program.Assert(!decision.Allowed, "开启核心保护时必须拦截 CLI restore");
        Program.Assert(decision.WarningMessage.Contains("CLI deskflow restore"), "警告文案应说明被拦截的动作");
    }

    private static void AllowsExternalRestoreWhenDisabled()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = false };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Server };
        var decision = DeskflowCoreGuard.EvaluateExternalStop(settings, status, new DeskflowStopRequest("CLI deskflow restore", "本机 CLI"));
        Program.Assert(decision.Allowed, "关闭核心保护后应允许 CLI restore");
    }

    private static void BlocksExternalJsonRoleOffTamper()
    {
        var trusted = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = true, RemoteHost = "10.10.3.136" };
        var tampered = trusted.Clone();
        tampered.Role = DeskflowRole.Off;
        var result = DeskflowCoreGuard.EvaluateExternalSettingsChange(trusted, tampered);
        Program.Assert(result.Blocked, "外部把 deskflow.json 的 Role 改为 Off 必须被拦截");
        Program.Assert(result.Effective.Role == DeskflowRole.Server, "拦截后必须保留原本受信任的角色");
        Program.Assert(result.Summary.Contains("角色改为关闭"), "摘要应指出角色被改为关闭");
    }

    private static void BlocksExternalJsonDisableGuardAndManaged()
    {
        var trusted = new DeskflowSettings { Role = DeskflowRole.Client, Managed = true, ProtectCoreFromExternalStop = true, RemoteHost = "10.10.3.136" };
        var tampered = trusted.Clone();
        tampered.Managed = false;
        tampered.ProtectCoreFromExternalStop = false;
        tampered.RemoteHost = "10.10.3.200";
        var result = DeskflowCoreGuard.EvaluateExternalSettingsChange(trusted, tampered);
        Program.Assert(result.Blocked, "外部关闭 Managed 或核心保护必须被拦截");
        Program.Assert(result.Effective.Managed, "拦截后必须维持 Managed=true");
        Program.Assert(result.Effective.ProtectCoreFromExternalStop, "拦截后必须维持 ProtectCoreFromExternalStop=true");
        Program.Assert(result.Effective.Role == DeskflowRole.Client, "拦截后必须维持原角色");
        Program.Assert(result.Effective.RemoteHost == "10.10.3.200", "非受保护字段不应被意外丢弃");
    }

    private static void AllowsBenignExternalJsonUpdate()
    {
        var trusted = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = true, RemoteHost = "10.10.3.136", LinkPeer = true };
        var benign = trusted.Clone();
        benign.LinkPeer = false;
        benign.RemoteHost = "10.10.3.199";
        var result = DeskflowCoreGuard.EvaluateExternalSettingsChange(trusted, benign);
        Program.Assert(!result.Blocked, "修改对端 IP 或联动开关不应被误拦");
        Program.Assert(!result.Effective.LinkPeer && result.Effective.RemoteHost == "10.10.3.199", "良性修改应生效");
    }

    private static void DetectsUnexpectedCoreKill()
    {
        var settings = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = true };
        var status = new DeskflowStatus { RunningRole = DeskflowRole.Off, MainCores = 0 };
        Program.Assert(DeskflowCoreGuard.ShouldRecoverUnexpectedStop(settings, status, DeskflowRole.Server), "运行中的核心突然消失时应触发即时恢复告警");
        Program.Assert(DeskflowCoreGuard.UnexpectedStopMessage(DeskflowRole.Server).Contains("[警告]"), "强杀告警文案应包含 [警告]");
    }

    private static void IgnoresNormalOffOrColdStart()
    {
        var offSettings = new DeskflowSettings { Role = DeskflowRole.Off, Managed = true, ProtectCoreFromExternalStop = true };
        var offStatus = new DeskflowStatus { RunningRole = DeskflowRole.Off };
        Program.Assert(!DeskflowCoreGuard.ShouldRecoverUnexpectedStop(offSettings, offStatus, DeskflowRole.Off), "期望关闭时不应报外部强杀");

        var serverSettings = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = true };
        Program.Assert(!DeskflowCoreGuard.ShouldRecoverUnexpectedStop(serverSettings, offStatus, DeskflowRole.Off), "尚未观测到核心启动时不应误报外部强杀");

        var unprotected = new DeskflowSettings { Role = DeskflowRole.Server, Managed = true, ProtectCoreFromExternalStop = false };
        Program.Assert(!DeskflowCoreGuard.ShouldRecoverUnexpectedStop(unprotected, offStatus, DeskflowRole.Server), "关闭核心保护时不应触发保护告警");
    }
}
