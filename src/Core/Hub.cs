using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MoonlightHub.Core;

/// <summary>Composition root: settings, driver client, instance manager, profile engine and watchdog.</summary>
public sealed class Hub : IDisposable
{
    public static Hub? Current { get; private set; }

    public HubSettings Settings { get; }
    public HubState State { get; }
    public ParsecVdd Vdd { get; }
    public SunshineInstanceManager Instances { get; }
    public ProfileEngine Engine { get; }
    public Watchdog Watchdog { get; }
    public PairingMonitor Pairing { get; }

    public event Action? Changed;
    /// <summary>A Moonlight client started pairing with an instance and Sunshine is waiting for the PIN.</summary>
    public event Action<PairingRequest>? PairingRequested;

    public bool Headless { get; }

    public Hub(bool headless = false)
    {
        Headless = headless;
        Paths.EnsureDataDirs();
        Settings = HubSettings.Load();
        State = HubState.Load();
        Vdd = new ParsecVdd { PingIntervalMs = Settings.VddPingMs };
        Instances = new SunshineInstanceManager(Settings);
        Engine = new ProfileEngine(Settings, State, Vdd, Instances);
        Watchdog = new Watchdog(Settings, State, Vdd, Instances, Engine, subscribeSystemEvents: !headless);
        Pairing = new PairingMonitor(Settings, Instances);
        Pairing.PairingRequested += req => { try { PairingRequested?.Invoke(req); } catch { } Changed?.Invoke(); };
        Pairing.PairingFinished += (_, _, _, _) => Changed?.Invoke();
        Pairing.ClientModeRequested += OnClientModeRequested;
        Pairing.ClientConnectionChanged += (_, _) => Changed?.Invoke();

        Vdd.StateChanged += () => Changed?.Invoke();
        Instances.Changed += () => Changed?.Invoke();
        Engine.Applied += () => Changed?.Invoke();
        Watchdog.Ticked += () => Changed?.Invoke();
        Current = this;
    }

    public Profile? ActiveProfile => Settings.Profile(State.ActiveProfileId) ?? Settings.Profile(Settings.ActiveProfileId);

    /// <summary>React immediately when a client launches a stream with a specific mode (the watchdog also covers this on its 5 s tick).</summary>
    private void OnClientModeRequested(int instanceId, ClientMode mode, bool sops)
    {
        State.LastClientMode[instanceId] = mode;
        State.LastClientFps[instanceId] = mode.Fps;
        State.Save();
        if (Engine.IsBusy) return;
        _ = Task.Run(() =>
        {
            try
            {
                var r = Engine.ApplyClientMode(instanceId, mode);
                if (r == true) Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn("按客户端请求调整虚拟屏失败: " + ex.Message);
            }
        });
    }

    /// <summary>Opens the driver, adopts running instances and (optionally) re-applies the active profile.</summary>
    public async Task StartupAsync(bool autoApply)
    {
        Log.Info($"Moonlight Hub 启动 (exe={Paths.ExePath}, 数据目录={Paths.DataDir})");
        if (ParsecVdd.IsDriverPresent())
        {
            if (Vdd.Open()) Log.Info($"Parsec VDD 已打开 (版本 0.{Vdd.Version})，keep-alive 运行中");
            else Log.Warn(Vdd.LastError ?? "Parsec VDD 打开失败");
        }
        else
        {
            Log.Warn("未检测到 Parsec Virtual Display Adapter，虚拟屏功能不可用");
        }

        foreach (var rt in Instances.All)
        {
            if (State.InstanceDd.TryGetValue(rt.Spec.Id, out var dd)) rt.DesiredDd = dd;
            if (State.InstanceOutputIds.TryGetValue(rt.Spec.Id, out var oid)) rt.DesiredOutputId = oid;
            if (rt.Spec.Enabled && Instances.Adopt(rt.Spec))
            {
                Log.Info($"接管已运行的实例 {rt.Spec.Id} (pid {rt.Pid})");
            }
        }
        await Instances.PollAsync().ConfigureAwait(false);

        if (autoApply && Settings.AutoApplyOnStart)
        {
            var profile = ActiveProfile;
            if (profile != null)
            {
                Log.Info($"启动时自动应用方案「{profile.Name}」(延迟 {Settings.StartupDelaySeconds}s)");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Settings.StartupDelaySeconds, 0, 120))).ConfigureAwait(false);
                await Engine.ApplyAsync(profile).ConfigureAwait(false);
            }
        }
        if (Settings.WatchdogEnabled) Watchdog.Start();
        if (!Headless) Pairing.Start();
        Changed?.Invoke();
    }

    /// <summary>Disables the legacy scheduled tasks and takes over their Sunshine process.</summary>
    public async Task<List<string>> TakeOverLegacyAsync()
    {
        var notes = new List<string>();
        foreach (var name in TaskSchedulerHelper.LegacyTaskNames)
        {
            var info = TaskSchedulerHelper.Query(name);
            if (!info.Exists) continue;
            var end = TaskSchedulerHelper.End(name);
            var dis = TaskSchedulerHelper.Disable(name);
            notes.Add($"{name}: 结束={(end.Ok ? "ok" : "跳过")} 禁用={(dis.Ok ? "ok" : dis.Message.Trim())}");
            Log.Info("旧任务处理: " + notes[^1]);
        }
        await Instances.StopAllSunshineProcessesAsync().ConfigureAwait(false);
        Settings.LegacyTasksTakenOver = true;
        Settings.Save();
        Changed?.Invoke();
        return notes;
    }

    /// <summary>Re-enables the legacy tasks and stops the hub-managed instances (rollback path).</summary>
    public async Task<List<string>> RestoreLegacyAsync()
    {
        var notes = new List<string>();
        Watchdog.Stop();
        foreach (var rt in Instances.All)
        {
            if (rt.IsAlive) await Instances.StopAsync(rt.Spec).ConfigureAwait(false);
        }
        foreach (var name in new[] { "Moonlight Parsec Display Controller", "Sunshine Connection Guard", "Sunshine User Session" })
        {
            if (!TaskSchedulerHelper.Query(name).Exists) continue;
            var en = TaskSchedulerHelper.Enable(name);
            notes.Add($"{name}: 启用={(en.Ok ? "ok" : en.Message.Trim())}");
        }
        var run = TaskSchedulerHelper.Run("Sunshine User Session");
        notes.Add("Sunshine User Session: 运行=" + (run.Ok ? "ok" : run.Message.Trim()));
        Settings.LegacyTasksTakenOver = false;
        Settings.Save();
        Log.Info("已恢复旧的自启动任务: " + string.Join("; ", notes));
        Changed?.Invoke();
        return notes;
    }

    /// <summary>IPv4 addresses a Moonlight client could use, most useful first.</summary>
    public static List<(string Address, string Interface)> GetHostAddresses()
    {
        var list = new List<(string, string, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    var desc = nic.Description;
                    var rank = desc.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || desc.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) || desc.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ? 0
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ? 2
                        : 5;
                    list.Add((ip, nic.Name, rank));
                }
            }
        }
        catch { }
        return list.OrderBy(t => t.Item3).Select(t => (t.Item1, t.Item2)).ToList();
    }

    public void Dispose()
    {
        Pairing.Dispose();
        Watchdog.Dispose();
        Vdd.Dispose();
    }
}
