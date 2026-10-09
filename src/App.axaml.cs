using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MoonlightHub.Core;
using MoonlightHub.UI;

namespace MoonlightHub;

public partial class App : Application
{
    public const string ExitEventName = InstanceControl.ExitEventName;

    private Hub? _hub;
    private HubTrayIcon? _tray;
    private MainWindow? _window;
    private readonly Dictionary<int, PairingWindow> _pairingWindows = new();
    private readonly Dictionary<int, StandbyPromptWindow> _standbyWindows = new();

    public static App Instance => (App)Current!;
    public Hub Hub => _hub!;
    public MainWindow MainWin => _window!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("未处理的界面异常", e.Exception);
                e.Handled = true;
            };

            if (OperatingSystem.IsWindows()) Log.Info($"进程权限: {(ProcessUtil.IsElevated() ? "管理员" : "普通用户")}");
            _hub = new Hub();
            _window = new MainWindow(_hub);
            _tray = new HubTrayIcon(_hub, _window);
            _hub.PairingRequested += req => Dispatcher.UIThread.Post(() =>
            {
                _tray?.Notify("Moonlight 配对", $"客户端 {req.RemoteAddress} 正在向实例 {req.InstanceId} 请求配对，请输入 PIN");
                if (_hub.Settings.AutoPairingPrompt) ShowPairingWindow(req);
            });

            _hub.Standby.Requested += prompt => Dispatcher.UIThread.Post(() => ShowStandbyPrompt(prompt));
            _hub.Standby.Resolved += (id, reason) => Dispatcher.UIThread.Post(() =>
            {
                if (_standbyWindows.TryGetValue(id, out var w)) w.Withdraw(reason);
            });

            if (!Program.Args.Contains("--minimized")) _window.Show();

            InstanceControl.Listen(
                () => Dispatcher.UIThread.Post(() => _window?.ShowFromTray()),
                OnExitForReplace);
            _ = Task.Run(async () =>
            {
                await _hub.StartupAsync(autoApply: true).ConfigureAwait(false);
                Program.HandoffVdd?.Dispose();
                Program.HandoffVdd = null;
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnExitForReplace()
    {
        Log.Info("收到热替换请求，退出但保留虚拟屏、Sunshine 实例与 Deskflow");
        // hard guarantee: the successor already pings the driver, so never linger if the UI thread is busy
        new Thread(() => { Thread.Sleep(4000); Log.Warn("热替换退出超时，强制结束进程"); Environment.Exit(0); }) { IsBackground = true }.Start();
        Dispatcher.UIThread.Post(() => ExitApplication(stopInstances: false, removeDisplays: false));
    }

    /// <summary>Asks whether a disconnected tablet's virtual display should be switched off.</summary>
    public void ShowStandbyPrompt(StandbyPrompt prompt)
    {
        if (_hub == null) return;
        if (_standbyWindows.TryGetValue(prompt.InstanceId, out var existing) && existing.IsVisible)
        {
            existing.Activate();
            return;
        }
        var window = new StandbyPromptWindow(_hub, prompt);
        _standbyWindows[prompt.InstanceId] = window;
        window.Closed += (_, _) => _standbyWindows.Remove(prompt.InstanceId);
        window.Show();
        window.Activate();
    }

    /// <summary>Shows (or refreshes) the PIN prompt for a pending pairing request.</summary>
    public void ShowPairingWindow(PairingRequest req)
    {
        if (_hub == null) return;
        var spec = _hub.Settings.Instance(req.InstanceId);
        if (spec == null) return;
        if (_pairingWindows.TryGetValue(req.InstanceId, out var existing) && existing.IsVisible)
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

    public void ExitApplication(bool stopInstances, bool removeDisplays = true)
    {
        if (!removeDisplays && !stopInstances)
        {
            // hand-over to a successor: stop our timers, leave the driver handle, Sunshine and Deskflow to it
            try
            {
                _hub?.Watchdog.Stop();
                _hub?.Pairing.Stop();
                _hub?.Deskflow.Detach();
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
            _hub?.Deskflow.Detach();
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

    private void Shutdown()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
