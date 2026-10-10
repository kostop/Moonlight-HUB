using System.Text.RegularExpressions;

namespace MoonlightHub.Core;

/// <summary>Operations that need administrator rights; the hub re-launches itself with "--elevated &lt;op&gt;".</summary>
public static class ElevatedOps
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return 1;
        try
        {
            switch (args[0])
            {
                case "add-parsec-mode":
                    {
                        var m = Regex.Match(args.Length > 1 ? args[1] : string.Empty, @"^(\d+)x(\d+)@(\d+)$");
                        if (!m.Success) return 1;
                        var (ok, msg) = ParsecModes.WriteElevated(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                        Log.Info("[elevated] add-parsec-mode: " + msg);
                        return ok ? 0 : 2;
                    }
                case "update-parsec-mode":
                    {
                        var m = Regex.Match(args.Length > 2 ? args[2] : string.Empty, @"^(\d+)x(\d+)@(\d+)$");
                        if (!m.Success || !int.TryParse(args[1], out var index)) return 1;
                        var (ok, msg) = ParsecModes.Update(index, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                        Log.Info("[elevated] update-parsec-mode: " + msg);
                        return ok ? 0 : 2;
                    }
                case "remove-parsec-mode":
                    {
                        if (args.Length < 2 || !int.TryParse(args[1], out var index)) return 1;
                        var (ok, msg) = ParsecModes.Remove(index);
                        Log.Info("[elevated] remove-parsec-mode: " + msg);
                        return ok ? 0 : 2;
                    }
                case "restart-parsec-adapter":
                    {
                        var state = ParsecVdd.QueryDeviceState();
                        var instance = string.IsNullOrEmpty(state.InstanceId) ? @"ROOT\DISPLAY\0000" : state.InstanceId;
                        if (state.Health == ParsecVdd.DeviceHealth.Disabled)
                        {
                            var (ce, oe) = ProcessUtil.Run("pnputil.exe", $"/enable-device \"{instance}\"", 60000);
                            Log.Info($"[elevated] pnputil /enable-device => {ce}: {oe.Replace(Environment.NewLine, " ")}");
                            return ce == 0 ? 0 : 2;
                        }
                        var (code, output) = ProcessUtil.Run("pnputil.exe", $"/restart-device \"{instance}\"", 60000);
                        Log.Info($"[elevated] pnputil /restart-device {instance} => {code}: {output.Replace(Environment.NewLine, " ")}");
                        // a device with problem code 43 sometimes survives /restart-device: verify and fall back to disable + enable
                        Thread.Sleep(1500);
                        if (code == 0 && ParsecVdd.QueryDeviceState().Health == ParsecVdd.DeviceHealth.Ok) return 0;
                        ProcessUtil.Run("pnputil.exe", $"/disable-device \"{instance}\"", 60000);
                        Thread.Sleep(1500);
                        var (code2, output2) = ProcessUtil.Run("pnputil.exe", $"/enable-device \"{instance}\"", 60000);
                        Log.Info($"[elevated] pnputil disable/enable => {code2}: {output2.Replace(Environment.NewLine, " ")}");
                        return code2 == 0 ? 0 : 2;
                    }
                case "install-parsec-driver":
                    {
                        var (ok, msg) = ParsecVdd.InstallDriver();
                        Log.Info("[elevated] install-parsec-driver: " + msg);
                        return ok ? 0 : 2;
                    }
                case "disable-fast-startup":
                    {
                        var (code, output) = ProcessUtil.Run("reg.exe", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power"" /v HiberbootEnabled /t REG_DWORD /d 0 /f", 15000);
                        Log.Info($"[elevated] disable-fast-startup => {code}: {output.Replace(Environment.NewLine, " ")}");
                        return code == 0 ? 0 : 2;
                    }
                case "disable-legacy-tasks":
                    {
                        var allOk = true;
                        foreach (var name in TaskSchedulerHelper.LegacyTaskNames)
                        {
                            if (!TaskSchedulerHelper.Query(name).Exists) continue;
                            TaskSchedulerHelper.End(name);
                            var r = TaskSchedulerHelper.Disable(name);
                            Log.Info($"[elevated] disable {name}: {(r.Ok ? "ok" : r.Message)}");
                            allOk &= r.Ok;
                        }
                        return allOk ? 0 : 2;
                    }
                case "enable-legacy-tasks":
                    {
                        var allOk = true;
                        foreach (var name in new[] { "Moonlight Parsec Display Controller", "Sunshine Connection Guard", "Sunshine User Session" })
                        {
                            if (!TaskSchedulerHelper.Query(name).Exists) continue;
                            var r = TaskSchedulerHelper.Enable(name);
                            allOk &= r.Ok;
                        }
                        return allOk ? 0 : 2;
                    }
                default:
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[elevated] 操作失败", ex);
            return 3;
        }
    }
}
