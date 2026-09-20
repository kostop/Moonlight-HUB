using System.Windows;
using System.Windows.Threading;
using MoonlightHub.Core;
using MoonlightHub.UI;

namespace MoonlightHub;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string ShowEventName = @"Local\MoonlightHub.ShowWindow";
    public const string ExitEventName = @"Local\MoonlightHub.ExitForReplace";
    private ParsecVdd? _handoffVdd;
    private Hub? _hub;
    private TrayIcon? _tray;
    private MainWindow? _window;

    public static App Instance => (App)Current;
    public Hub Hub => _hub!;
    public MainWindow MainWin => _window!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length > 0 && args[0] == "--elevated")
        {
            Environment.Exit(ElevatedOps.Run(args.Skip(1).ToArray()));
            return;
        }
        if (args.Length > 0 && args[0] == "--cli")
        {
            Environment.Exit(Cli.Run(args.Skip(1).ToArray()));
            return;
        }
        if (args.Length > 1 && args[0] == "--send-ctrlc" && int.TryParse(args[1], out var targetPid))
        {
            // Helper mode: deliver Ctrl+C to a console process and leave. The event also reaches this helper, which is fine.
            var ok = ProcessUtil.SendCtrlC(targetPid);
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        var replace = args.Contains("--replace");

        // Run with administrator rights (like ParsecVDisplay does) so the Parsec adapter can be restarted / installed and
        // custom modes registered without UAC prompts. The logon task already starts us elevated; a manual launch or the
        // hot-update script re-launches through ShellExecute "runas" before touching the driver or the old instance.
        if (!args.Contains("--no-elevate") && !ProcessUtil.IsElevated() && HubSettings.Load().RunElevated)
        {
            if (!replace && !IsFirstInstance())
            {
                try { using var evt = EventWaitHandle.OpenExisting(ShowEventName); evt.Set(); } catch { }
                Shutdown();
                return;
            }
            var forwarded = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            if (ProcessUtil.StartSelfElevatedDetached(forwarded))
            {
                Log.Info("以管理员权限重新启动 Moonlight Hub" + (forwarded.Length > 0 ? " (" + forwarded + ")" : string.Empty));
                Shutdown();
                return;
            }
            Log.Warn("无法以管理员权限启动（UAC 被取消），以普通权限继续运行：驱动异常时需要手动确认提权");
        }

        if (replace)
        {
            // Hot replace: keep the driver alive from this process before asking the old instance to leave,
            // so the virtual displays (and the Moonlight sessions on them) survive the hand-over.
            _handoffVdd = new ParsecVdd();
            if (_handoffVdd.Open()) Log.Info("热替换: 新进程已接管 VDD keep-alive");
            try
            {
                using var exitEvt = EventWaitHandle.OpenExisting(ExitEventName);
                exitEvt.Set();
            }
            catch { }
        }

        _mutex = new Mutex(false, @"Local\MoonlightHub.SingleInstance");
        var createdNew = false;
        try
        {
            createdNew = _mutex.WaitOne(replace ? 20000 : 0);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }
        if (!createdNew && replace)
        {
            // The old instance did not leave in time. We already keep the driver alive, so take over by force
            // rather than leaving a stale copy running from a deleted staging folder.
            Log.Warn("热替换: 旧实例未在 20 秒内退出，强制结束旧实例并接管");
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("MoonlightHub"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                p.Dispose();
            }
            try { createdNew = _mutex.WaitOne(10000); }
            catch (AbandonedMutexException) { createdNew = true; }
        }
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("未处理的界面异常", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("未处理的异常: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("后台任务异常", ex.Exception); ex.SetObserved(); };

        Log.Info($"进程权限: {(ProcessUtil.IsElevated() ? "管理员" : "普通用户")}");
        _hub = new Hub();
        _window = new MainWindow(_hub);
        _tray = new TrayIcon(_hub, _window);
        _hub.PairingRequested += req => Dispatcher.BeginInvoke(() =>
        {
            _tray?.ShowBalloon("Moonlight 配对", $"客户端 {req.RemoteAddress} 正在向实例 {req.InstanceId} 请求配对，请输入 PIN");
            if (_hub.Settings.AutoPairingPrompt) ShowPairingWindow(req);
        });

        var minimized = args.Contains("--minimized");
        if (!minimized)
        {
            _window.Show();
        }

        StartShowListener();
        StartExitListener();
        _ = Task.Run(async () =>
        {
            await _hub.StartupAsync(autoApply: true).ConfigureAwait(false);
            _handoffVdd?.Dispose();
            _handoffVdd = null;
        });
    }

    /// <summary>True when no other hub instance holds the single-instance mutex (the probe releases it again immediately).</summary>
    private static bool IsFirstInstance()
    {
        try
        {
            using var probe = new Mutex(false, @"Local\MoonlightHub.SingleInstance");
            bool got;
            try { got = probe.WaitOne(0); }
            catch (AbandonedMutexException) { got = true; }
            if (got) probe.ReleaseMutex();
            return got;
        }
        catch
        {
            return true;
        }
    }

    private void StartExitListener()
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var thread = new Thread(() =>
        {
            evt.WaitOne();
            Log.Info("收到热替换请求，退出但保留虚拟屏与 Sunshine 实例");
            // hard guarantee: the successor already pings the driver, so never linger if the UI thread is busy
            new Thread(() => { Thread.Sleep(4000); Log.Warn("热替换退出超时，强制结束进程"); Environment.Exit(0); }) { IsBackground = true }.Start();
            Dispatcher.BeginInvoke(() => ExitApplication(stopInstances: false, removeDisplays: false));
        }) { IsBackground = true, Name = "ExitListener" };
        thread.Start();
    }

    private readonly Dictionary<int, PairingWindow> _pairingWindows = new();

    /// <summary>Shows (or refreshes) the PIN prompt for a pending pairing request.</summary>
    public void ShowPairingWindow(PairingRequest req)
    {
        if (_hub == null) return;
        var spec = _hub.Settings.Instance(req.InstanceId);
        if (spec == null) return;
        if (_pairingWindows.TryGetValue(req.InstanceId, out var existing) && existing.IsLoaded)
        {
            existing.UpdateRequest(req);
            existing.Activate();
            return;
        }
        var window = new PairingWindow(_hub, spec, req);
        _pairingWindows[req.InstanceId] = window;
        window.Closed += (_, _) => _pairingWindows.Remove(req.InstanceId);
        window.Show();
        window.Activate();
    }

    private void StartShowListener()
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                evt.WaitOne();
                Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
            }
        }) { IsBackground = true, Name = "ShowListener" };
        thread.Start();
    }

    public void ExitApplication(bool stopInstances, bool removeDisplays = true)
    {
        if (!removeDisplays && !stopInstances)
        {
            // hand-over to a successor: stop our timers, leave the driver handle to be closed by process exit
            try
            {
                _hub?.Watchdog.Stop();
                _hub?.Pairing.Stop();
                _tray?.Dispose();
            }
            catch { }
            Log.Info("Moonlight Hub 退出（热替换）");
            new Thread(() => { Thread.Sleep(1500); Environment.Exit(0); }) { IsBackground = true }.Start();
            Shutdown();
            return;
        }
        try
        {
            _hub?.Watchdog.Stop();
            if (stopInstances && _hub != null)
            {
                foreach (var rt in _hub.Instances.All)
                {
                    if (rt.IsAlive) _hub.Instances.StopAsync(rt.Spec).GetAwaiter().GetResult();
                }
            }
            if (removeDisplays && _hub != null && _hub.Vdd.IsOpen)
            {
                try { _hub.Vdd.RemoveAll(); } catch { }
            }
            _tray?.Dispose();
            _hub?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("退出时出错", ex);
        }
        Log.Info("Moonlight Hub 退出");
        Shutdown();
    }
}
