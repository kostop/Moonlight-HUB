using Avalonia;
using MoonlightHub.Core;

namespace MoonlightHub;

public static class Program
{
    /// <summary>Command-line switches the UI instance was started with (read by <see cref="App"/>).</summary>
    public static string[] Args { get; private set; } = Array.Empty<string>();

    /// <summary>Held for the lifetime of the UI instance.</summary>
    internal static Mutex? SingleInstanceMutex;
    /// <summary>Hot replace: this process keeps the Parsec driver alive until the hub has started.</summary>
    internal static ParsecVdd? HandoffVdd;

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;

        if (args.Length > 0 && args[0] == "--elevated") return ElevatedOps.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--cli") return Cli.Run(args.Skip(1).ToArray());
        if (args.Length > 1 && args[0] == "--send-ctrlc" && int.TryParse(args[1], out var targetPid))
        {
            // helper mode: deliver Ctrl+C to a console process and leave (the event also reaches this helper, which is fine)
            return ProcessUtil.SendCtrlC(targetPid) ? 0 : 1;
        }

        var replace = args.Contains("--replace");

        // Session 0 (services, SSH logins) has no desktop, and its Local\ objects are not the ones of the hub on the
        // user's desktop: a hub started there would run blind next to the real one and fight it over the Parsec driver,
        // the Sunshine ports and Deskflow. Only --cli works from there.
        if (OperatingSystem.IsWindows() && System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
        {
            const string message = "Moonlight Hub 不能在会话 0（服务 / SSH 会话）里运行界面：那里没有桌面，而且会和桌面会话里的 Hub 争用虚拟屏驱动。"
                + "请在用户的桌面会话里启动，例如 schtasks /run /tn \"Moonlight Hub\"，或用一次性计划任务（交互用户、最高权限）运行 MoonlightHub.exe --replace。命令行功能请用 --cli。";
            Log.Warn(message);
            try { Console.Error.WriteLine(message); } catch { }
            return 3;
        }

        // Windows: run with administrator rights so the Parsec adapter can be restarted / installed and custom modes
        // registered without UAC prompts. The logon task already starts us elevated; a manual launch or the hot-update
        // script re-launches through ShellExecute "runas" before touching the driver or the old instance.
        if (OperatingSystem.IsWindows() && !args.Contains("--no-elevate") && !ProcessUtil.IsElevated() && HubSettings.Load().RunElevated)
        {
            if (!replace && !InstanceControl.IsFirstInstance())
            {
                InstanceControl.SignalShow();
                return 0;
            }
            var forwarded = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            if (ProcessUtil.StartSelfElevatedDetached(forwarded))
            {
                Log.Info("以管理员权限重新启动 Moonlight Hub" + (forwarded.Length > 0 ? " (" + forwarded + ")" : string.Empty));
                return 0;
            }
            Log.Warn("无法以管理员权限启动（UAC 被取消），以普通权限继续运行：驱动异常时需要手动确认提权");
        }

        if (replace)
        {
            if (OperatingSystem.IsWindows())
            {
                // keep the driver alive from this process before asking the old instance to leave,
                // so the virtual displays (and the Moonlight sessions on them) survive the hand-over
                HandoffVdd = new ParsecVdd();
                if (HandoffVdd.Open()) Log.Info("热替换: 新进程已接管 VDD keep-alive");
            }
            InstanceControl.SignalExitForReplace();
        }

        SingleInstanceMutex = new Mutex(false, InstanceControl.MutexName);
        bool owned;
        try { owned = SingleInstanceMutex.WaitOne(replace ? 20000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned && replace)
        {
            // the old instance did not leave in time: take over by force rather than leaving a stale copy running
            Log.Warn("热替换: 旧实例未在 20 秒内退出，强制结束旧实例并接管");
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("MoonlightHub"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                p.Dispose();
            }
            try { owned = SingleInstanceMutex.WaitOne(10000); }
            catch (AbandonedMutexException) { owned = true; }
        }
        if (!owned)
        {
            InstanceControl.SignalShow();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("未处理的异常: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("后台任务异常", ex.Exception); ex.SetObserved(); };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { EnableIme = true })
        .LogToTrace();
}
