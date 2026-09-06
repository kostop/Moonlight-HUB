using System.IO;
using System.Security.Principal;
using System.Text;

namespace MoonlightHub.Core;

/// <summary>Thin wrapper over schtasks.exe for the hub's own logon task and for the legacy tasks it replaces.</summary>
public static class TaskSchedulerHelper
{
    public const string HubTaskName = "Moonlight Hub";

    /// <summary>The scheduled tasks of the old PowerShell-based setup that the hub supersedes.</summary>
    public static readonly string[] LegacyTaskNames =
    {
        "Moonlight Parsec Display Controller",
        "Sunshine Connection Guard",
        "Sunshine User Session",
        "Sunshine after Parsec display",
    };

    public sealed record TaskInfo(string Name, bool Exists, string Status);

    public static TaskInfo Query(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Query /TN \"{name}\" /FO LIST", 15000);
        if (code != 0) return new TaskInfo(name, false, "不存在");
        var status = "未知";
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            var idx = t.IndexOf(':');
            if (idx <= 0) continue;
            var key = t[..idx].Trim();
            var value = t[(idx + 1)..].Trim();
            if (key is "Status" or "状态")
            {
                status = value;
            }
        }
        return new TaskInfo(name, true, status);
    }

    public static (bool Ok, string Message) Disable(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Change /TN \"{name}\" /DISABLE", 15000);
        return (code == 0, output);
    }

    public static (bool Ok, string Message) Enable(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Change /TN \"{name}\" /ENABLE", 15000);
        return (code == 0, output);
    }

    public static (bool Ok, string Message) End(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/End /TN \"{name}\"", 15000);
        return (code == 0, output);
    }

    public static (bool Ok, string Message) Run(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Run /TN \"{name}\"", 15000);
        return (code == 0, output);
    }

    public static (bool Ok, string Message) Delete(string name)
    {
        var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Delete /TN \"{name}\" /F", 15000);
        return (code == 0, output);
    }

    public static bool IsHubAutostartRegistered() => Query(HubTaskName).Exists;

    /// <summary>Registers a logon task (interactive token, no time limit, limited privileges) that starts the hub minimized.</summary>
    public static (bool Ok, string Message) RegisterHubAutostart()
    {
        var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? string.Empty;
        var exe = Paths.ExePath;
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Moonlight Hub 副屏中心：保持 Parsec 虚拟屏、Sunshine 多实例与显示布局。</Description>
    <URI>\{HubTaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(user.Name)}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{Escape(sid)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{Escape(exe)}</Command>
      <Arguments>--minimized</Arguments>
      <WorkingDirectory>{Escape(Path.GetDirectoryName(exe) ?? string.Empty)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        var tmp = Path.Combine(Path.GetTempPath(), $"moonlight-hub-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            var (code, output) = ProcessUtil.Run("schtasks.exe", $"/Create /TN \"{HubTaskName}\" /XML \"{tmp}\" /F", 20000);
            if (code == 0)
            {
                Log.Info("已注册开机自启任务 " + HubTaskName);
                return (true, "已注册登录自启任务");
            }
            return (false, output);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static (bool Ok, string Message) UnregisterHubAutostart()
    {
        if (!Query(HubTaskName).Exists) return (true, "任务不存在");
        var r = Delete(HubTaskName);
        if (r.Ok) Log.Info("已删除开机自启任务 " + HubTaskName);
        return r;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
