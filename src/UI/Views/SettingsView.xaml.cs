using System.Windows;
using System.Windows.Controls;
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
        LoadFromSettings();
    }

    private Hub Hub => _window.Hub;
    private HubSettings S => Hub.Settings;

    private void LoadFromSettings()
    {
        SunshineExe.Text = S.SunshineExe;
        Instance1Dir.Text = S.Instance1ConfigDir;
        InstancesRoot.Text = S.InstancesRoot;
        ScriptsDir.Text = S.ScriptsDir;
        ParsecExe.Text = S.ParsecVDisplayExe;
        AutoApply.IsChecked = S.AutoApplyOnStart;
        WatchdogEnabled.IsChecked = S.WatchdogEnabled;
        HighPriority.IsChecked = S.HighProcessPriority;
        CloseToTray.IsChecked = S.CloseToTray;
        AutoCreds.IsChecked = S.AutoManageCredentials;
        AutoPairPrompt.IsChecked = S.AutoPairingPrompt;
        PingMs.Text = S.VddPingMs.ToString();
        WatchdogSec.Text = S.WatchdogSeconds.ToString();
        StartupDelay.Text = S.StartupDelaySeconds.ToString();
        BuildProfiles();
        BuildInstances();
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
                var registered = TaskSchedulerHelper.IsHubAutostartRegistered();
                Dispatcher.BeginInvoke(() =>
                {
                    _autostartSyncing = true;
                    AutoStartBox.IsChecked = registered;
                    _autostartSyncing = false;
                    AutostartText.Text = "开机自启：" + (registered ? $"已注册计划任务「{TaskSchedulerHelper.HubTaskName}」，路径 {Paths.ExePath}" : "未注册");
                });
            });
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_autostartSyncing || !_loaded) return;
        var enable = AutoStartBox.IsChecked == true;
        AutostartText.Text = enable ? "正在注册…" : "正在取消…";
        _ = Task.Run(() =>
        {
            var r = enable ? TaskSchedulerHelper.RegisterHubAutostart() : TaskSchedulerHelper.UnregisterHubAutostart();
            Dispatcher.BeginInvoke(() =>
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
            var del = Ui.B("删除", (_, _) =>
            {
                if (MessageBox.Show(_window, $"删除方案「{captured.Name}」？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                S.Profiles.Remove(captured);
                S.Save();
                BuildProfiles();
            }, "DangerButton");
            del.Margin = new Thickness(0);
            actions.Children.Add(del);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var info = Ui.Row(Ui.Icon(p.Glyph, Ui.Res("AccentBrush")), new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, Width = 170, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = p.ModeText + (p.VirtualDisplays.Count > 0 ? " · " + string.Join(", ", p.VirtualDisplays.Take(p.VirtualCount).Select(v => $"#{v.Slot + 1} {v.ModeText}→实例{v.InstanceId}")) : string.Empty), Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(info);
            ProfilesPanel.Children.Add(Ui.Card(row, true));
        }
    }

    private void EditProfile(Profile p)
    {
        var dlg = new ProfileEditorWindow(p.Clone(), S) { Owner = _window };
        if (dlg.ShowDialog() != true) return;
        var idx = S.Profiles.IndexOf(p);
        if (idx >= 0) S.Profiles[idx] = dlg.Result; else S.Profiles.Add(dlg.Result);
        S.Save();
        BuildProfiles();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = new Profile { Id = Guid.NewGuid().ToString("N")[..8], Name = "新方案", Mode = ProfileMode.Extend, VirtualDisplays = { new VirtualDisplaySpec { Slot = 0, InstanceId = 1 } } };
        var dlg = new ProfileEditorWindow(p, S) { Owner = _window };
        if (dlg.ShowDialog() != true) return;
        S.Profiles.Add(dlg.Result);
        S.Save();
        BuildProfiles();
    }

    private void ResetProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(_window, "用默认的 6 个方案替换当前列表？", "恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            name.TextChanged += (_, _) => captured.Name = name.Text.Trim();
            port.TextChanged += (_, _) => { if (int.TryParse(port.Text, out var v) && v is >= 1030 and <= 65500) captured.Port = v; };
            fps.TextChanged += (_, _) => { if (int.TryParse(fps.Text, out var v) && v is >= 0 and <= 1000) captured.MinimumFpsTarget = v; };
            audio.Checked += (_, _) => captured.Audio = true; audio.Unchecked += (_, _) => captured.Audio = false;
            tray.Checked += (_, _) => captured.SystemTray = true; tray.Unchecked += (_, _) => captured.SystemTray = false;
            enabled.Checked += (_, _) => captured.Enabled = true; enabled.Unchecked += (_, _) => captured.Enabled = false;
            var row = Ui.Row(Ui.Pill($"实例 {inst.Id}", inst.Id == 1 ? "accent" : "muted"), Ui.T("名称", "Muted"), name, Ui.T("端口", "Muted"), port, Ui.T("最低帧率", "Muted"), fps, audio, tray, enabled);
            if (inst.Id != 1)
            {
                var del = Ui.B("删除", (_, _) =>
                {
                    if (MessageBox.Show(_window, $"删除实例 {captured.Id}？（配置目录不会被删除）", "删除实例", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    S.Instances.Remove(captured);
                    S.Save();
                    BuildInstances();
                }, "DangerButton");
                del.Padding = new Thickness(8, 3, 8, 3);
                row.Children.Add(del);
            }
            row.Margin = new Thickness(0, 3, 0, 3);
            InstancesPanel.Children.Add(row);
        }
    }

    private void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        var id = S.Instances.Count == 0 ? 1 : S.Instances.Max(i => i.Id) + 1;
        S.Instances.Add(new InstanceSpec { Id = id, Name = $"Desktop-lin-{id}", Port = 47989 + 1000 * (id - 1), MinimumFpsTarget = 60 });
        S.Save();
        BuildInstances();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        S.SunshineExe = SunshineExe.Text.Trim();
        S.Instance1ConfigDir = Instance1Dir.Text.Trim();
        S.InstancesRoot = InstancesRoot.Text.Trim();
        S.ScriptsDir = ScriptsDir.Text.Trim();
        S.ParsecVDisplayExe = ParsecExe.Text.Trim();
        S.AutoApplyOnStart = AutoApply.IsChecked == true;
        S.WatchdogEnabled = WatchdogEnabled.IsChecked == true;
        S.HighProcessPriority = HighPriority.IsChecked == true;
        S.CloseToTray = CloseToTray.IsChecked == true;
        S.AutoManageCredentials = AutoCreds.IsChecked == true;
        S.AutoPairingPrompt = AutoPairPrompt.IsChecked == true;
        if (int.TryParse(PingMs.Text, out var ping)) S.VddPingMs = Math.Clamp(ping, 10, 90);
        if (int.TryParse(WatchdogSec.Text, out var wd)) S.WatchdogSeconds = Math.Clamp(wd, 2, 120);
        if (int.TryParse(StartupDelay.Text, out var sd)) S.StartupDelaySeconds = Math.Clamp(sd, 0, 120);
        Hub.Vdd.PingIntervalMs = S.VddPingMs;
        S.Save();
        if (S.WatchdogEnabled) Hub.Watchdog.Start(); else Hub.Watchdog.Stop();
        SaveState.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        Log.Info("设置已保存");
    }

}
