using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private bool _loaded;
    private DateTime _lastAutostart = DateTime.MinValue;

    public SettingsView(MainWindow window)
    {
        _window = window;
        InitializeComponent();

        // Sunshine paths/instances, profiles, Parsec/driver options, elevation, VDD ping and the Task Scheduler autostart
        // only make sense on Windows; the Linux build keeps the generic settings (close to tray, autostart).
        var win = OperatingSystem.IsWindows();
        ProfilesCard.IsVisible = win;
        InstancesCard.IsVisible = win;
        PathsCard.IsVisible = win;
        WinRunTop.IsVisible = win;
        WinRunBottom.IsVisible = win;
        if (!win) AutoStartBox.Content = "开机自动启动 Moonlight Hub（登录桌面后自动运行）";

        LoadFromSettings();
    }

    private Hub Hub => _window.Hub;
    private HubSettings S => Hub.Settings;

    private void LoadFromSettings()
    {
        CloseToTray.IsChecked = S.CloseToTray;
        if (OperatingSystem.IsWindows())
        {
            SunshineExe.Text = S.SunshineExe;
            Instance1Dir.Text = S.Instance1ConfigDir;
            InstancesRoot.Text = S.InstancesRoot;
            ScriptsDir.Text = S.ScriptsDir;
            ParsecExe.Text = S.ParsecVDisplayExe;
            AutoApply.IsChecked = S.AutoApplyOnStart;
            WatchdogEnabled.IsChecked = S.WatchdogEnabled;
            ConfirmStandby.IsChecked = S.ConfirmStandbyOnDisconnect;
            HighPriority.IsChecked = S.HighProcessPriority;
            AutoCreds.IsChecked = S.AutoManageCredentials;
            AutoPairPrompt.IsChecked = S.AutoPairingPrompt;
            UseEmbedded.IsChecked = S.UseEmbeddedSunshine;
            UseEmbedded.IsEnabled = Paths.HasEmbeddedSunshine;
            EmbeddedText.Text = Paths.HasEmbeddedSunshine
                ? (S.SunshineIsEmbedded ? $"当前使用随附的 Sunshine：{Paths.EmbeddedSunshineExe}；配置目录 {S.Instance1ConfigDir}" : $"随附的 Sunshine 位于 {Paths.EmbeddedSunshineExe}，当前使用的是 {S.SunshineExe}")
                : "本程序目录下没有 sunshine\\sunshine.exe（源码运行或精简版），使用下面指定的 Sunshine";
            RunElevated.IsChecked = S.RunElevated;
            AutoRestartAdapter.IsChecked = S.AutoRestartAdapter;
            AutoInstallDriver.IsChecked = S.AutoInstallDriver;
            ElevationText.Text = ProcessUtil.IsElevated()
                ? "当前进程：管理员权限"
                : "当前进程：普通权限（勾选并保存后，下次启动时以管理员权限运行；开机自启任务会同步改为管理员权限）";
            PingMs.Text = S.VddPingMs.ToString();
            WatchdogSec.Text = S.WatchdogSeconds.ToString();
            StartupDelay.Text = S.StartupDelaySeconds.ToString();
            BuildProfiles();
            BuildInstances();
        }
        _loaded = true;
    }

    private bool _autostartSyncing;

    public void Refresh()
    {
        if ((DateTime.UtcNow - _lastAutostart) > TimeSpan.FromSeconds(20))
        {
            _lastAutostart = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var task = TaskSchedulerHelper.QueryHubTask();
                    var registered = task != null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _autostartSyncing = true;
                        AutoStartBox.IsChecked = registered;
                        _autostartSyncing = false;
                        AutostartText.Text = "开机自启：" + (task != null ? $"已注册计划任务「{TaskSchedulerHelper.HubTaskName}」（{(task.Value.Highest ? "管理员权限" : "普通权限")}），路径 {task.Value.Exe}" : "未注册");
                    });
                }
                else
                {
                    var enabled = LinuxAutostart.IsEnabled();
                    Dispatcher.UIThread.Post(() =>
                    {
                        _autostartSyncing = true;
                        AutoStartBox.IsChecked = enabled;
                        _autostartSyncing = false;
                        AutostartText.Text = "开机自启：" + (enabled ? "已启用" : "未启用");
                    });
                }
            });
        }
    }

    private void AutoStart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_autostartSyncing || !_loaded) return;
        var enable = AutoStartBox.IsChecked == true;
        AutostartText.Text = enable ? "正在注册…" : "正在取消…";
        _ = Task.Run(() =>
        {
            (bool Ok, string Message) r;
            if (OperatingSystem.IsWindows())
                r = enable ? TaskSchedulerHelper.RegisterHubAutostart(S.RunElevated) : TaskSchedulerHelper.UnregisterHubAutostart();
            else
                r = LinuxAutostart.Set(enable);
            Dispatcher.UIThread.Post(() =>
            {
                _lastAutostart = DateTime.MinValue;
                Refresh();
                if (!r.Ok) _window.Toast(enable ? "注册失败" : "取消失败", r.Message, true);
            });
        });
    }

    private void BuildProfiles()
    {
        ProfilesPanel.Children.Clear();
        foreach (var p in S.Profiles)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var captured = p;
            actions.Children.Add(Ui.B("编辑", (_, _) => EditProfile(captured), "GhostButton"));
            actions.Children.Add(Ui.B("复制", (_, _) =>
            {
                var c = captured.Clone();
                c.Id = Guid.NewGuid().ToString("N")[..8];
                c.Name = captured.Name + " 副本";
                S.Profiles.Add(c);
                S.Save();
                BuildProfiles();
            }, "GhostButton"));
            var del = Ui.B("删除", async (_, _) =>
            {
                if (!await Dialogs.Confirm(_window, $"删除方案「{captured.Name}」？", "删除")) return;
                S.Profiles.Remove(captured);
                S.Save();
                BuildProfiles();
            }, "DangerButton");
            del.Margin = new Thickness(0);
            actions.Children.Add(del);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var detail = Ui.T(p.ModeText + (p.VirtualDisplays.Count > 0 ? " · " + string.Join(", ", p.VirtualDisplays.Take(p.VirtualCount).Select(v => $"#{v.Slot + 1} {v.ModeText}→实例{v.InstanceId}")) : string.Empty), "Muted");
            detail.VerticalAlignment = VerticalAlignment.Center;
            var info = Ui.Row(Ui.Icon(p.Glyph, Ui.Res("AccentBrush")), new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, Width = 170, VerticalAlignment = VerticalAlignment.Center },
                detail);
            row.Children.Add(info);
            ProfilesPanel.Children.Add(Ui.Card(row, true));
        }
    }

    private async void EditProfile(Profile p)
    {
        var dlg = new ProfileEditorWindow(p.Clone(), S);
        await dlg.ShowDialog(_window);
        if (!dlg.Accepted) return;
        var idx = S.Profiles.IndexOf(p);
        if (idx >= 0) S.Profiles[idx] = dlg.Result; else S.Profiles.Add(dlg.Result);
        S.Save();
        BuildProfiles();
    }

    private async void NewProfile_Click(object? sender, RoutedEventArgs e)
    {
        var p = new Profile { Id = Guid.NewGuid().ToString("N")[..8], Name = "新方案", Mode = ProfileMode.Extend, VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 } } };
        var dlg = new ProfileEditorWindow(p, S);
        await dlg.ShowDialog(_window);
        if (!dlg.Accepted) return;
        S.Profiles.Add(dlg.Result);
        S.Save();
        BuildProfiles();
    }

    private async void ResetProfiles_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.Confirm(_window, "用默认的 6 个方案替换当前列表？", "恢复默认")) return;
        S.Profiles = HubSettings.CreateDefault().Profiles;
        S.Save();
        BuildProfiles();
    }

    private void BuildInstances()
    {
        InstancesPanel.Children.Clear();
        foreach (var inst in S.Instances.OrderBy(i => i.Id))
        {
            var captured = inst;
            var name = new TextBox { Text = inst.Name, Width = 150, Margin = new Thickness(0, 0, 8, 0) };
            var port = new TextBox { Text = inst.Port.ToString(), Width = 70, Margin = new Thickness(0, 0, 8, 0) };
            var fps = new TextBox { Text = inst.MinimumFpsTarget.ToString(), Width = 50, Margin = new Thickness(0, 0, 8, 0) };
            var audio = new CheckBox { Content = "音频", IsChecked = inst.Audio, Margin = new Thickness(0, 0, 10, 0) };
            var tray = new CheckBox { Content = "托盘图标", IsChecked = inst.SystemTray, Margin = new Thickness(0, 0, 10, 0) };
            var enabled = new CheckBox { Content = "启用", IsChecked = inst.Enabled, Margin = new Thickness(0, 0, 10, 0) };
            name.TextChanged += (_, _) => captured.Name = (name.Text ?? string.Empty).Trim();
            port.TextChanged += (_, _) => { if (int.TryParse(port.Text, out var v) && v is >= 1030 and <= 65500) captured.Port = v; };
            fps.TextChanged += (_, _) => { if (int.TryParse(fps.Text, out var v) && v is >= 0 and <= 1000) captured.MinimumFpsTarget = v; };
            audio.IsCheckedChanged += (_, _) => captured.Audio = audio.IsChecked == true;
            tray.IsCheckedChanged += (_, _) => captured.SystemTray = tray.IsChecked == true;
            enabled.IsCheckedChanged += (_, _) => captured.Enabled = enabled.IsChecked == true;
            var row = Ui.Row(Ui.Pill($"实例 {inst.Id}", inst.Id == 1 ? "accent" : "muted"), Ui.T("名称", "Muted"), name, Ui.T("端口", "Muted"), port, Ui.T("最低帧率", "Muted"), fps, audio, tray, enabled);
            if (inst.Id != 1)
            {
                var del = Ui.B("删除", async (_, _) =>
                {
                    if (!await Dialogs.Confirm(_window, $"删除实例 {captured.Id}？（配置目录不会被删除）", "删除实例")) return;
                    S.Instances.Remove(captured);
                    S.Save();
                    BuildInstances();
                }, "DangerButton");
                del.Padding = new Thickness(8, 3, 8, 3);
                row.Children.Add(del);
            }
            // labels/inputs are vertically centred in the row (Avalonia stretches TextBlocks to the top of the row otherwise)
            foreach (var child in row.Children) child.VerticalAlignment = VerticalAlignment.Center;
            row.Margin = new Thickness(0, 3, 0, 3);
            InstancesPanel.Children.Add(row);
        }
    }

    private void AddInstance_Click(object? sender, RoutedEventArgs e)
    {
        var id = S.Instances.Count == 0 ? 1 : S.Instances.Max(i => i.Id) + 1;
        S.Instances.Add(new InstanceSpec { Id = id, Name = $"{Environment.MachineName}-{id}", Port = 47989 + 1000 * (id - 1), MinimumFpsTarget = 60 });
        S.Save();
        BuildInstances();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        S.CloseToTray = CloseToTray.IsChecked == true;
        if (OperatingSystem.IsWindows()) ApplyWindowsSettings();
        S.Save();
        if (OperatingSystem.IsWindows())
        {
            if (S.WatchdogEnabled) Hub.Watchdog.Start(); else Hub.Watchdog.Stop();
        }
        SaveState.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        Log.Info("设置已保存");
    }

    /// <summary>Reads the Windows-only controls (Sunshine paths, watchdog, elevation, VDD ping, …) back into the settings.</summary>
    private void ApplyWindowsSettings()
    {
        S.SunshineExe = (SunshineExe.Text ?? string.Empty).Trim();
        S.Instance1ConfigDir = (Instance1Dir.Text ?? string.Empty).Trim();
        S.InstancesRoot = (InstancesRoot.Text ?? string.Empty).Trim();
        S.ScriptsDir = (ScriptsDir.Text ?? string.Empty).Trim();
        S.ParsecVDisplayExe = (ParsecExe.Text ?? string.Empty).Trim();
        S.AutoApplyOnStart = AutoApply.IsChecked == true;
        S.WatchdogEnabled = WatchdogEnabled.IsChecked == true;
        S.ConfirmStandbyOnDisconnect = ConfirmStandby.IsChecked == true;
        S.HighProcessPriority = HighPriority.IsChecked == true;
        S.AutoManageCredentials = AutoCreds.IsChecked == true;
        S.AutoPairingPrompt = AutoPairPrompt.IsChecked == true;
        var elevationChanged = S.RunElevated != (RunElevated.IsChecked == true);
        S.RunElevated = RunElevated.IsChecked == true;
        S.AutoRestartAdapter = AutoRestartAdapter.IsChecked == true;
        S.AutoInstallDriver = AutoInstallDriver.IsChecked == true;
        var embeddedChanged = S.UseEmbeddedSunshine != (UseEmbedded.IsChecked == true);
        S.UseEmbeddedSunshine = UseEmbedded.IsChecked == true;
        if (embeddedChanged)
        {
            if (!S.UseEmbeddedSunshine && S.SunshineIsEmbedded && File.Exists(@"D:\YingYong\Sunshine\sunshine.exe"))
            {
                S.SunshineExe = @"D:\YingYong\Sunshine\sunshine.exe";
                S.Instance1ConfigDir = @"D:\YingYong\Sunshine\config";
                S.InstancesRoot = @"D:\YingYong\Sunshine\instances";
            }
            S.ResolveSunshinePaths();
            SunshineExe.Text = S.SunshineExe;
            Instance1Dir.Text = S.Instance1ConfigDir;
            InstancesRoot.Text = S.InstancesRoot;
            _window.Toast("Sunshine", "Sunshine 路径已切换，重新应用方案后实例会从新位置启动");
        }
        if (elevationChanged && TaskSchedulerHelper.IsHubAutostartRegistered())
        {
            _ = Task.Run(() => TaskSchedulerHelper.RegisterHubAutostart(S.RunElevated));
        }
        if (int.TryParse(PingMs.Text, out var ping)) S.VddPingMs = Math.Clamp(ping, 10, 90);
        if (int.TryParse(WatchdogSec.Text, out var wd)) S.WatchdogSeconds = Math.Clamp(wd, 2, 120);
        if (int.TryParse(StartupDelay.Text, out var sd)) S.StartupDelaySeconds = Math.Clamp(sd, 0, 120);
        Hub.Vdd.PingIntervalMs = S.VddPingMs;
    }
}
