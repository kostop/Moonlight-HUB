namespace MoonlightHub.Core;

/// <summary>一次来自外部（CLI / 本地 API / 远程请求）的停止 Deskflow 核心请求。</summary>
public sealed record DeskflowStopRequest(string Action, string Source, string? Caller = null);

/// <summary>核心保护对一次外部停止请求的裁决结果。</summary>
public sealed record DeskflowGuardDecision(bool Allowed, string WarningMessage, string LogMessage);

/// <summary>对外部直接改写 deskflow.json 的校验结果。</summary>
public sealed record DeskflowExternalEditResult(bool Blocked, DeskflowSettings Effective, string Summary, string WarningMessage);

/// <summary>
/// Deskflow 核心保护策略：拦截命令行、本地接口和外部篡改配置文件关闭 deskflow-core，
/// 并在核心被外部强杀时触发立即恢复告警，避免脚本或 AI Agent 擅自中断跨电脑键鼠共享。
/// </summary>
public static class DeskflowCoreGuard
{
    /// <summary>评估一次外部关闭/回退请求是否应被拦截。</summary>
    /// <param name="settings">当前受信任的 Deskflow 设置。</param>
    /// <param name="status">当前观测到的 Deskflow 运行状态。</param>
    /// <param name="request">外部请求的动作、来源与调用方信息。</param>
    public static DeskflowGuardDecision EvaluateExternalStop(DeskflowSettings settings, DeskflowStatus status, DeskflowStopRequest request)
    {
        if (!settings.ProtectCoreFromExternalStop)
            return new DeskflowGuardDecision(true, string.Empty, string.Empty);

        var isRestore = request.Action.Contains("restore", StringComparison.OrdinalIgnoreCase);
        var activeRole = status.RunningRole != DeskflowRole.Off ? status.RunningRole : settings.Role;
        if (!isRestore && activeRole == DeskflowRole.Off)
            return new DeskflowGuardDecision(true, string.Empty, string.Empty);

        var callerPart = string.IsNullOrWhiteSpace(request.Caller) ? string.Empty : $"，调用方：{request.Caller}";
        var peerPart = status.Peers.Count > 0 ? $"，已连接 {string.Join(", ", status.Peers)}" : string.Empty;
        var rolePart = activeRole == DeskflowRole.Off ? "已托管" : $"{DeskflowManager.RoleName(activeRole)}模式{peerPart}";

        var warning =
            $"[警告] 已拦截关闭 Deskflow 核心（deskflow-core）的外部请求（动作：{request.Action}，来源：{request.Source}{callerPart}）。\n" +
            $"本机 Moonlight Hub 已开启「Deskflow 核心保护」（当前：{rolePart}），禁止外部命令行、本地接口、脚本或 AI Agent 直接关闭或回退 deskflow-core。\n" +
            "如果本机自动化或 AI 需要使用鼠标/键盘但光标正位于另一台电脑上，禁止通过关闭 Deskflow 核心、修改配置或强杀进程来把鼠标拉回——这会直接中断用户正在另一台电脑上的工作。\n" +
            "请停止重试，告知用户当前情况，由用户本人切回光标或在 Moonlight Hub 界面手动操作。";

        var log = $"Deskflow 核心保护: 已拦截外部请求（动作={request.Action}，来源={request.Source}{callerPart}，当前={rolePart}）";
        return new DeskflowGuardDecision(false, warning, log);
    }

    /// <summary>检查外部修改后的 deskflow.json 是否试图关闭核心、取消托管或关闭核心保护。</summary>
    /// <param name="trusted">Hub 内存中受信任的当前设置。</param>
    /// <param name="loaded">从磁盘新读到的外部修改后的设置。</param>
    public static DeskflowExternalEditResult EvaluateExternalSettingsChange(DeskflowSettings trusted, DeskflowSettings loaded)
    {
        if (!trusted.ProtectCoreFromExternalStop)
            return new DeskflowExternalEditResult(false, loaded, string.Empty, string.Empty);

        var violations = new List<string>();
        if (!loaded.ProtectCoreFromExternalStop) violations.Add("关闭核心保护开关");
        if (trusted.Managed && !loaded.Managed) violations.Add("取消 Hub 托管");
        if (trusted.Role != DeskflowRole.Off && loaded.Role == DeskflowRole.Off) violations.Add("将角色改为关闭");

        if (violations.Count == 0)
            return new DeskflowExternalEditResult(false, loaded, string.Empty, string.Empty);

        var effective = loaded.Clone();
        effective.ProtectCoreFromExternalStop = true;
        effective.Managed = trusted.Managed;
        effective.Role = trusted.Role;

        var summary = string.Join("、", violations);
        var warning = $"[警告] 已拦截外部直接修改 deskflow.json（试图{summary}），已自动恢复受保护的配置。如需关闭 Deskflow 核心，请在 Moonlight Hub 界面手动操作。";
        return new DeskflowExternalEditResult(true, effective, summary, warning);
    }

    /// <summary>判断运行中的 deskflow-core 是否在未经 Hub 关闭的情况下意外消失（如被外部强杀）。</summary>
    /// <param name="settings">当前受信任的 Deskflow 设置。</param>
    /// <param name="status">本次巡检观测到的最新运行状态。</param>
    /// <param name="lastObservedRunningRole">上一次巡检时观测到的运行角色。</param>
    public static bool ShouldRecoverUnexpectedStop(DeskflowSettings settings, DeskflowStatus status, DeskflowRole lastObservedRunningRole) =>
        settings.Managed
        && settings.ProtectCoreFromExternalStop
        && settings.Role != DeskflowRole.Off
        && lastObservedRunningRole == settings.Role
        && status.RunningRole == DeskflowRole.Off;

    /// <summary>生成 deskflow-core 被外部终止时的告警文案。</summary>
    /// <param name="role">原本期望运行的 Deskflow 角色。</param>
    public static string UnexpectedStopMessage(DeskflowRole role) =>
        $"[警告] 检测到 deskflow-core（{DeskflowManager.RoleName(role)}）在运行中被外部进程终止或异常退出，已拦截中断并立即重新拉起守护。";
}
