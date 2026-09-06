using System.Text;
using MoonlightHub.Native;

namespace MoonlightHub.Core;

/// <summary>
/// Headless command line (MoonlightHub.exe --cli …) for scripting and for testing the core without the UI.
/// Note: virtual displays only live while a process pings the driver, so "vdd add" is paired with "--hold".
/// </summary>
public static class Cli
{
    private static readonly StringBuilder Out = new();

    private static void P(string s = "") => Out.AppendLine(s);

    public static int Run(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        var code = 1;
        try
        {
            code = RunCore(args);
        }
        catch (Exception ex)
        {
            P("错误: " + ex);
        }
        var text = Out.ToString();
        Console.Out.Write(Environment.NewLine + text);
        Console.Out.Flush();
        // Belt and braces: never leave a headless process hanging around after the work is done.
        new Thread(() => { Thread.Sleep(4000); try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { } }) { IsBackground = true }.Start();
        return code;
    }

    private static int RunCore(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }
        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        var hold = 0;
        var holdIdx = rest.FindIndex(a => a == "--hold");
        if (holdIdx >= 0 && holdIdx + 1 < rest.Count && int.TryParse(rest[holdIdx + 1], out var h))
        {
            hold = h;
            rest.RemoveRange(holdIdx, 2);
        }

        using var hub = new Hub(headless: true);
        switch (cmd)
        {
            case "help":
                PrintHelp();
                return 0;

            case "status":
                PrintStatus(hub);
                return 0;

            case "displays":
                PrintDisplays(DisplayManager.Enumerate());
                return 0;

            case "gdi":
                foreach (var (name, desc, flags) in DisplayManager.EnumerateGdiSources())
                    P($"{name,-14} flags=0x{flags:X8} {desc}");
                return 0;

            case "modes":
                if (rest.Count == 0)
                {
                    foreach (var m in ParsecModes.Read()) P($"#{m.Index}: {m}");
                    return 0;
                }
                if (rest[0] == "add" && rest.Count > 1 && TryParseMode(rest[1], out var mw, out var mh, out var mhz))
                {
                    var (ok, msg) = ParsecModes.EnsureRegistered(mw, mh, mhz);
                    P(msg);
                    return ok ? 0 : 2;
                }
                if (rest[0] == "list" && rest.Count > 1)
                {
                    foreach (var m in DisplayManager.GetModes(rest[1])) P(m.ToString());
                    return 0;
                }
                if (rest[0] == "remove" && rest.Count > 1 && int.TryParse(rest[1], out var ri))
                {
                    var (ok, msg) = ParsecModes.Remove(ri);
                    P(msg);
                    return ok ? 0 : 2;
                }
                P("用法: modes | modes add WxH@Hz | modes remove <index> | modes list \\\\.\\DISPLAYn");
                return 1;

            case "vdd":
                return VddCommand(hub, rest, hold);

            case "apply":
                {
                    if (rest.Count == 0) { P("需要方案 id"); return 1; }
                    var profile = hub.Settings.Profile(rest[0]);
                    if (profile == null) { P("未知方案: " + rest[0] + "，可用: " + string.Join(", ", hub.Settings.Profiles.Select(p => p.Id))); return 1; }
                    hub.Engine.Progress += s => Console.WriteLine("  " + s);
                    var result = hub.Engine.ApplyAsync(profile).GetAwaiter().GetResult();
                    P(result.Summary);
                    PrintDisplays(result.Displays);
                    Hold(hub, hold);
                    return result.Success ? 0 : 2;
                }

            case "health":
                {
                    var hr = hub.Engine.CheckHealth();
                    P($"virtual displays ok={hr.VirtualDisplaysOk} ({hr.ParsecActive}/{hr.ParsecExpected}) missing instances=[{string.Join(",", hr.MissingInstances)}] drifted=[{string.Join(",", hr.DriftedInstances)}]");
                    return 0;
                }

            case "instances":
                hub.Instances.PollAsync().GetAwaiter().GetResult();
                foreach (var rt in hub.Instances.All)
                    P($"实例 {rt.Spec.Id} {rt.Spec.Name,-16} port={rt.Spec.Port} pid={rt.Pid} state={rt.State} info={(rt.Info?.Reachable == true ? rt.Info.State : "-")} err={rt.LastError}");
                return 0;

            case "start":
            case "stop":
            case "restart":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null) return 1;
                    if (cmd == "start") P(hub.Instances.StartAsync(spec).GetAwaiter().GetResult() ? "已启动" : "启动失败: " + hub.Instances.Get(spec).LastError);
                    else if (cmd == "stop") { hub.Instances.StopAsync(spec).GetAwaiter().GetResult(); P("已停止"); }
                    else P(hub.Instances.RestartAsync(spec).GetAwaiter().GetResult() ? "已重启" : "重启失败");
                    return 0;
                }

            case "config":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null) return 1;
                    var outputId = rest.Count > 1 ? rest[1] : hub.State.InstanceOutputIds.GetValueOrDefault(spec.Id, string.Empty);
                    var changed = hub.Instances.EnsureConfig(spec, outputId, out var msg);
                    P($"{msg} (changed={changed}) -> {hub.Instances.Get(spec).ConfigPath}");
                    return 0;
                }

            case "serverinfo":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null) return 1;
                    var info = SunshineApi.GetServerInfoAsync(spec.Port).GetAwaiter().GetResult();
                    P($"reachable={info.Reachable} hostname={info.Hostname} state={info.State} version={info.AppVersion} currentgame={info.CurrentGame}");
                    return 0;
                }

            case "creds":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null || rest.Count < 3) { P("用法: creds <实例id> <用户名> <密码>"); return 1; }
                    var (ok, msg) = hub.Instances.SetCredentialsAsync(spec, rest[1], rest[2]).GetAwaiter().GetResult();
                    P(msg);
                    return ok ? 0 : 2;
                }

            case "auth-test":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null) return 1;
                    var (ok, msg) = hub.Instances.Api(spec).TestAuthAsync().GetAwaiter().GetResult();
                    P(msg);
                    return ok ? 0 : 2;
                }

            case "pair":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null || rest.Count < 2) { P("用法: pair <实例id> <PIN> [客户端名]"); return 1; }
                    var (ok, msg) = hub.Instances.Api(spec).PairAsync(rest[1], rest.Count > 2 ? rest[2] : "Moonlight").GetAwaiter().GetResult();
                    P(msg);
                    return ok ? 0 : 2;
                }

            case "clients":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null) return 1;
                    foreach (var c in hub.Instances.Api(spec).ListClientsAsync().GetAwaiter().GetResult()) P($"{c.Name} {c.Uuid}");
                    return 0;
                }

            case "unpair":
                {
                    var spec = SpecArg(hub, rest);
                    if (spec == null || rest.Count < 2) { P("用法: unpair <实例id> <客户端名或uuid>"); return 1; }
                    var api = hub.Instances.Api(spec);
                    var clients = api.ListClientsAsync().GetAwaiter().GetResult();
                    var targets = clients.Where(c => c.Uuid.Equals(rest[1], StringComparison.OrdinalIgnoreCase) || c.Name.Equals(rest[1], StringComparison.OrdinalIgnoreCase)).ToList();
                    if (targets.Count == 0) { P("没有匹配的客户端"); return 2; }
                    foreach (var t in targets)
                    {
                        var (ok, msg) = api.UnpairAsync(t.Uuid).GetAwaiter().GetResult();
                        P($"{t.Name} ({t.Uuid}): {msg}");
                        if (!ok) return 2;
                    }
                    return 0;
                }

            case "takeover":
                foreach (var n in hub.TakeOverLegacyAsync().GetAwaiter().GetResult()) P(n);
                return 0;

            case "restore":
                foreach (var n in hub.RestoreLegacyAsync().GetAwaiter().GetResult()) P(n);
                return 0;

            case "tasks":
                foreach (var name in TaskSchedulerHelper.LegacyTaskNames.Append(TaskSchedulerHelper.HubTaskName))
                {
                    var t = TaskSchedulerHelper.Query(name);
                    P($"{name,-40} exists={t.Exists} status={t.Status}");
                }
                return 0;

            case "autostart":
                {
                    if (rest.Count == 0) { P(TaskSchedulerHelper.IsHubAutostartRegistered() ? "已注册" : "未注册"); return 0; }
                    var r = rest[0] == "on" ? TaskSchedulerHelper.RegisterHubAutostart() : TaskSchedulerHelper.UnregisterHubAutostart();
                    P(r.Message);
                    return r.Ok ? 0 : 2;
                }

            case "topology":
                {
                    if (rest.Count == 0) { P("用法: topology extend|clone|internal|external"); return 1; }
                    var flag = rest[0] switch
                    {
                        "clone" => NativeMethods.SDC_TOPOLOGY_CLONE,
                        "internal" => NativeMethods.SDC_TOPOLOGY_INTERNAL,
                        "external" => NativeMethods.SDC_TOPOLOGY_EXTERNAL,
                        _ => NativeMethods.SDC_TOPOLOGY_EXTEND
                    };
                    P("SetDisplayConfig => " + DisplayManager.SetTopology(flag));
                    return 0;
                }

            case "pairwatch":
                {
                    // Follow the instance logs and print pairing / mode events for --hold seconds (test aid).
                    hub.Pairing.PairingRequested += r => Console.WriteLine($"  [pairing] 实例 {r.InstanceId} 来自 {r.RemoteAddress} uniqueid={r.UniqueId}");
                    hub.Pairing.PairingFinished += (id, uid, ok, name) => Console.WriteLine($"  [pairing] 实例 {id} {(ok ? "成功" : "失败")} {name} ({uid})");
                    hub.Pairing.PairingCancelled += id => Console.WriteLine($"  [pairing] 实例 {id} 会话结束");
                    hub.Pairing.ClientModeRequested += (id, m, sops) => Console.WriteLine($"  [mode] 实例 {id} 请求 {m} sops={sops}");
                    hub.Pairing.Start();
                    Hold(hub, hold > 0 ? hold : 30);
                    hub.Pairing.Stop();
                    P("pending: " + string.Join(", ", hub.Pairing.Pending.Select(kv => $"{kv.Key}<-{kv.Value.RemoteAddress}")));
                    return 0;
                }

            case "adapter":
                {
                    if (rest.Count > 0 && rest[0] == "restart")
                    {
                        var (ok, msg) = ParsecVdd.RestartAdapter();
                        P(msg);
                        return ok ? 0 : 2;
                    }
                    P("Parsec adapter status: " + ParsecVdd.QueryAdapterStatus());
                    return 0;
                }

            case "shutdown":
                {
                    try
                    {
                        using var evt = EventWaitHandle.OpenExisting(App.ExitEventName);
                        evt.Set();
                        P("已通知运行中的 Moonlight Hub 退出（保留虚拟屏与实例）");
                        return 0;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        P("没有运行中的 Moonlight Hub");
                        return 2;
                    }
                }

            case "addresses":
                foreach (var (ip, nic) in Hub.GetHostAddresses()) P($"{ip,-16} {nic}");
                return 0;

            default:
                P("未知命令: " + cmd);
                PrintHelp();
                return 1;
        }
    }

    private static int VddCommand(Hub hub, List<string> rest, int hold)
    {
        var vdd = hub.Vdd;
        var sub = rest.Count > 0 ? rest[0] : "status";
        if (sub == "status")
        {
            P($"driver present={ParsecVdd.IsDriverPresent()} path={ParsecVdd.FindDevicePath()}");
            if (vdd.Open()) P($"version=0.{vdd.Version} pinging={vdd.IsPinging}");
            else P("open failed: " + vdd.LastError);
            PrintDisplays(DisplayManager.Enumerate().Where(d => d.IsParsec).ToList());
            return 0;
        }
        if (!vdd.Open()) { P("open failed: " + vdd.LastError); return 2; }
        switch (sub)
        {
            case "add":
                {
                    var idx = vdd.AddDisplay();
                    P($"AddDisplay => slot {idx}");
                    var list = DisplayManager.WaitFor(l => l.Any(d => d.IsParsec && d.Uid == 256 + idx), TimeSpan.FromSeconds(20));
                    PrintDisplays(list);
                    Hold(hub, hold);
                    if (hold > 0)
                    {
                        vdd.RemoveDisplay(idx);
                        P("removed after hold");
                    }
                    return 0;
                }
            case "remove":
                {
                    var idx = rest.Count > 1 ? int.Parse(rest[1]) : 0;
                    vdd.RemoveDisplay(idx);
                    P($"RemoveDisplay({idx}) sent");
                    Thread.Sleep(1500);
                    PrintDisplays(DisplayManager.Enumerate().Where(d => d.IsParsec).ToList());
                    return 0;
                }
            case "removeall":
                vdd.RemoveAll();
                P("RemoveAll sent");
                Thread.Sleep(1500);
                PrintDisplays(DisplayManager.Enumerate().Where(d => d.IsParsec).ToList());
                return 0;
            case "hold":
                Hold(hub, hold > 0 ? hold : 30);
                return 0;
            case "replug":
                {
                    var slot = rest.Count > 1 ? int.Parse(rest[1]) : 0;
                    hub.Instances.PollAsync().GetAwaiter().GetResult();
                    hub.Engine.Progress += s => Console.WriteLine("  " + s);
                    var (ok, msg) = hub.Engine.ReplugAsync(slot, "命令行").GetAwaiter().GetResult();
                    P(msg);
                    PrintDisplays(DisplayManager.Enumerate().Where(d => d.IsParsec).ToList());
                    return ok ? 0 : 2;
                }
            default:
                P("用法: vdd status|add [--hold s]|remove <slot>|removeall|replug <slot>|hold --hold s");
                return 1;
        }
    }

    private static void Hold(Hub hub, int seconds)
    {
        if (seconds <= 0) return;
        Console.WriteLine($"  holding for {seconds}s (keep-alive running, Ctrl+C to abort)…");
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(1000);
        }
        P($"hold finished; ping failures={hub.Vdd.ConsecutivePingFailures} last ping {(DateTime.UtcNow - hub.Vdd.LastPingUtc).TotalMilliseconds:0}ms ago");
    }

    private static InstanceSpec? SpecArg(Hub hub, List<string> rest)
    {
        if (rest.Count == 0 || !int.TryParse(rest[0], out var id)) { P("需要实例 id (1..n)"); return null; }
        var spec = hub.Settings.Instance(id);
        if (spec == null) P("未知实例 " + id);
        return spec;
    }

    private static bool TryParseMode(string text, out int w, out int h, out int hz)
    {
        w = h = hz = 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+)x(\d+)@(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value);
        h = int.Parse(m.Groups[2].Value);
        hz = int.Parse(m.Groups[3].Value);
        return true;
    }

    private static void PrintStatus(Hub hub)
    {
        P($"settings: {Paths.SettingsFile}");
        P($"sunshine: {hub.Settings.SunshineExe}");
        P($"active profile: {hub.State.ActiveProfileId} (expected virtual displays {hub.State.ExpectedVirtualDisplays}, instances [{string.Join(",", hub.State.ExpectedInstances)}])");
        P($"vdd driver present: {ParsecVdd.IsDriverPresent()}");
        P("profiles: " + string.Join(", ", hub.Settings.Profiles.Select(p => $"{p.Id}={p.Name}")));
        PrintDisplays(DisplayManager.Enumerate());
        hub.Instances.PollAsync().GetAwaiter().GetResult();
        foreach (var rt in hub.Instances.All)
            P($"实例 {rt.Spec.Id} {rt.Spec.Name,-16} port={rt.Spec.Port} pid={rt.Pid} state={rt.State}");
    }

    private static void PrintDisplays(List<DisplayInfo> displays)
    {
        P($"displays ({displays.Count}):");
        foreach (var d in displays)
        {
            P($"  {d.GdiName,-14} {(d.Active ? "active " : "inactive")} {(d.Primary ? "PRIMARY" : "       ")} {d.Label,-22} {d.ModeText,-20} pos=({d.PositionX},{d.PositionY}) uid={d.Uid} slot={d.ParsecSlot} adapter={d.AdapterId} target={d.TargetId} id={d.SunshineDeviceId}");
        }
    }

    private static void PrintHelp()
    {
        P("MoonlightHub --cli <命令>");
        P("  status | displays | gdi | health | addresses | tasks");
        P("  vdd status | vdd add [--hold 秒] | vdd remove <槽位> | vdd removeall | vdd hold --hold 秒");
        P("  modes | modes add WxH@Hz | modes list \\\\.\\DISPLAYn");
        P("  apply <方案id> [--hold 秒]");
        P("  instances | start <id> | stop <id> | restart <id> | config <id> [output_id] | serverinfo <id>");
        P("  creds <id> <用户> <密码> | auth-test <id> | pair <id> <PIN> [名称] | clients <id>");
        P("  takeover | restore | autostart [on|off] | topology extend|clone|internal|external");
        P("  shutdown  (让运行中的 Hub 退出但保留虚拟屏；配合 MoonlightHub.exe --minimized --replace 做无中断更新)");
    }
}
