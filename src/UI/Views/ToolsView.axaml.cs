using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class ToolsView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private DateTime _lastTasks = DateTime.MinValue;

    public ToolsView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
    }

    private Hub Hub => _window.Hub;

    private string Script(string name)
    {
        var dir = Hub.Settings.ScriptsDir;
        var root = Paths.RepoRoot;
        var candidates = new[]
        {
            Path.Combine(dir, name),
            Path.Combine(root, "scripts", name),
            Path.Combine(root, "scripts", "sunshine", name),
            Path.Combine(root, "deskflow", "touch", name),
            Path.Combine(root, "deskflow-touch", name),
            Path.Combine(root, name),
        };
        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(dir, name);
    }

    public void Refresh()
    {
        UsbToggleBtn.IsEnabled = UsbRepairBtn.IsEnabled = UsbCheckBtn.IsEnabled = File.Exists(Script("toggle-usb-network.ps1"));
        TouchOnBtn.IsEnabled = TouchOffBtn.IsEnabled = File.Exists(Script("touch-mouse-isolation.ps1"));
        if ((DateTime.UtcNow - _lastTasks) > TimeSpan.FromSeconds(30))
        {
            _lastTasks = DateTime.UtcNow;
            FastStartupBtn.IsEnabled = FastStartupEnabled();
            if (!FastStartupBtn.IsEnabled) FastStartupText.Text = "快速启动已关闭：每次开机驱动都会完整初始化。";
            _ = Task.Run(() =>
            {
                var parts = TaskSchedulerHelper.LegacyTaskNames.Select(n => { var t = TaskSchedulerHelper.Query(n); return t.Exists ? $"{n}: {t.Status}" : null; }).Where(s => s != null).ToList();
                var hub = TaskSchedulerHelper.Query(TaskSchedulerHelper.HubTaskName);
                var text = (parts.Count == 0 ? "没有找到旧任务" : string.Join("  ·  ", parts)) + $"\nMoonlight Hub 自启: {(hub.Exists ? hub.Status : "未注册")}" + (Hub.Settings.LegacyTasksTakenOver ? "  ·  已接管" : "  ·  尚未接管");
                Dispatcher.UIThread.Post(() => LegacyText.Text = text);
            });
        }
    }

    private void RunScript(string name, string args)
    {
        var path = Script(name);
        if (!File.Exists(path)) { _window.Toast("脚本不存在", path, true); return; }
        Output.Text = $"> {name} {args}\n运行中…";
        _ = Task.Run(() =>
        {
            var (code, output) = ProcessUtil.RunPowerShell(path, args + " -NoDialog", 180000);
            Log.Info($"脚本 {name} {args} 退出码 {code}");
            Dispatcher.UIThread.Post(() => Output.Text = $"> {name} {args}\n退出码 {code}\n{output}");
        });
    }

    private void UsbToggle_Click(object? sender, RoutedEventArgs e) => RunScript("toggle-usb-network.ps1", string.Empty);
    private void UsbRepair_Click(object? sender, RoutedEventArgs e) => RunScript("toggle-usb-network.ps1", "-RepairCurrentMode");
    private void UsbCheck_Click(object? sender, RoutedEventArgs e) => RunScript("toggle-usb-network.ps1", "-CheckOnly");
    private void TouchOn_Click(object? sender, RoutedEventArgs e) => RunScript("touch-mouse-isolation.ps1", "-Action Enable");
    private void TouchOff_Click(object? sender, RoutedEventArgs e) => RunScript("touch-mouse-isolation.ps1", "-Action Disable");

    private async void TakeOver_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.Confirm(_window, "将结束并禁用旧的计划任务（Parsec 控制脚本、Sunshine 守护、Sunshine 用户会话），并停止当前运行的 Sunshine，然后由 Moonlight Hub 重新按当前方案启动。继续？", "接管")) return;
        Output.Text = "接管中…";
        Ui.RunAsync(_window, async () =>
        {
            var notes = await Hub.TakeOverLegacyAsync().ConfigureAwait(false);
            var profile = Hub.ActiveProfile;
            if (profile != null) await Hub.Engine.ApplyAsync(profile).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => { Output.Text = string.Join("\n", notes); _lastTasks = DateTime.MinValue; Refresh(); });
        }, "接管完成", "旧任务已禁用，当前方案已重新应用");
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.Confirm(_window, "恢复旧的计划任务并停止 Hub 管理的实例？（用于回退到之前的脚本方案）", "恢复", danger: true)) return;
        Ui.RunAsync(_window, async () =>
        {
            var notes = await Hub.RestoreLegacyAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => { Output.Text = string.Join("\n", notes); _lastTasks = DateTime.MinValue; Refresh(); });
        }, "已恢复", "旧任务已重新启用");
    }

    private void Tasks_Click(object? sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            var sb = new StringBuilder();
            foreach (var name in TaskSchedulerHelper.LegacyTaskNames.Append(TaskSchedulerHelper.HubTaskName))
            {
                var t = TaskSchedulerHelper.Query(name);
                sb.AppendLine($"{name}: {(t.Exists ? t.Status : "不存在")}");
            }
            Dispatcher.UIThread.Post(() => Output.Text = sb.ToString());
        });
    }

    private void OpenScripts_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Directory.Exists(Hub.Settings.ScriptsDir) ? Hub.Settings.ScriptsDir : Paths.RepoRoot);
    private void OpenSunshine_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Hub.Settings.SunshineDir);

    private static bool FastStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return key?.GetValue("HiberbootEnabled") is int v && v != 0;
        }
        catch { return false; }
    }

    private async void FastStartup_Click(object? sender, RoutedEventArgs e)
    {
        if (!FastStartupEnabled()) { _window.Toast("快速启动", "快速启动已经是关闭状态"); return; }
        var prompt = ProcessUtil.IsElevated() ? string.Empty : "（会弹出 UAC 提示）";
        if (!await Dialogs.Confirm(_window, "关闭 Windows 快速启动？下次开机会完整初始化驱动，开机时间略长。" + prompt, "快速启动")) return;
        Ui.RunAsync(_window, () => Task.Run(() =>
        {
            var code = ProcessUtil.IsElevated() ? ElevatedOps.Run(new[] { "disable-fast-startup" }) : ProcessUtil.RunSelfElevated("--elevated disable-fast-startup");
            if (code != 0) throw new InvalidOperationException(code == -1 ? "用户取消了 UAC 提示" : $"退出码 {code}");
            Dispatcher.UIThread.Post(() => FastStartupText.Text = "快速启动已关闭（HiberbootEnabled = 0）。");
        }), "快速启动", "已关闭");
    }
    private void OpenWeb_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell($"https://localhost:{(Hub.Settings.Instance(1)?.WebPort ?? 47990)}/");
    private void OpenParsec_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Hub.Settings.ParsecVDisplayExe);
    private void OpenData_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Paths.DataDir);
    private void OpenDisplaySettings_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell("ms-settings:display");

    private void Diag_Click(object? sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Moonlight Hub 诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"exe: {Paths.ExePath}");
            sb.AppendLine($"settings: {Paths.SettingsFile}");
            sb.AppendLine($"active profile: {Hub.State.ActiveProfileId} expected vdd={Hub.State.ExpectedVirtualDisplays} instances=[{string.Join(",", Hub.State.ExpectedInstances)}]");
            sb.AppendLine($"vdd: present={ParsecVdd.IsDriverPresent()} open={Hub.Vdd.IsOpen} version=0.{Hub.Vdd.Version} pinging={Hub.Vdd.IsPinging} failures={Hub.Vdd.ConsecutivePingFailures}");
            sb.AppendLine("displays:");
            foreach (var d in DisplayManager.Enumerate()) sb.AppendLine("  " + d);
            sb.AppendLine("gdi sources:");
            foreach (var (n, desc, flags) in DisplayManager.EnumerateGdiSources()) sb.AppendLine($"  {n} 0x{flags:X8} {desc}");
            sb.AppendLine("parsec modes: " + string.Join(", ", ParsecModes.Read()));
            sb.AppendLine("instances:");
            foreach (var rt in Hub.Instances.All) sb.AppendLine($"  {rt.Spec.Id} {rt.Spec.Name} port={rt.Spec.Port} pid={rt.Pid} state={rt.State} err={rt.LastError} cfg={rt.ConfigPath}");
            var df = Hub.Deskflow.Refresh();
            sb.AppendLine($"deskflow: wanted={Hub.Deskflow.Settings.Role} managed={Hub.Deskflow.Settings.Managed} running={df.RunningRole} pid={df.CorePid} filtered={df.Filtered} filterOk={df.FilterHealthy} f24={df.F24Rule} listening={df.Listening} peers=[{string.Join(",", df.Peers)}] conf={df.ConfiguredRole} remote={df.RemoteHost} cores={df.MainCores} legacyTakenOver={Hub.Deskflow.Settings.LegacyTakenOver}");
            sb.AppendLine("addresses: " + string.Join(", ", Hub.GetHostAddresses().Select(a => $"{a.Address}({a.Interface})")));
            sb.AppendLine("tasks:");
            foreach (var name in TaskSchedulerHelper.LegacyTaskNames.Append(TaskSchedulerHelper.HubTaskName)) { var t = TaskSchedulerHelper.Query(name); sb.AppendLine($"  {name}: {(t.Exists ? t.Status : "不存在")}"); }
            sb.AppendLine();
            sb.AppendLine("recent log:");
            sb.AppendLine(Log.TailFile(200));
            var path = Path.Combine(Paths.DataDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Dispatcher.UIThread.Post(() => { Output.Text = sb.ToString(); _window.Toast("诊断报告", "已保存到 " + path); });
        });
    }
}
