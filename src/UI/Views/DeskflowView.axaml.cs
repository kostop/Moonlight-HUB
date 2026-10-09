using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class DeskflowView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private bool _loadingOptions;
    private bool _hostEdited;
    private DateTime _lastLog = DateTime.MinValue;
    private DateTime _lastLegacy = DateTime.MinValue;

    public DeskflowView() : this(App.Instance.MainWin) { }

    public DeskflowView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        RemoteHostBox.TextChanged += (_, _) => _hostEdited = RemoteHostBox.IsKeyboardFocusWithin || _hostEdited;
        // touch filter and keyboard-release monitor only exist in the Windows setup
        FilterBox.IsVisible = MonitorBox.IsVisible = TouchIsolationBox.IsVisible = OperatingSystem.IsWindows();
        if (!OperatingSystem.IsWindows())
        {
            PeerHint.Text = "保存后，本机的客户端连接和对端 Hub 联动都改用这个地址（共享屏幕布局服务就运行在本机）。";
        }
        LoadOptions();
    }

    private DeskflowManager Df => _window.Hub.Deskflow;

    private void LoadOptions()
    {
        _loadingOptions = true;
        var s = Df.Settings;
        ManagedBox.IsChecked = s.Managed;
        LinkBox.IsChecked = s.LinkPeer;
        FilterBox.IsChecked = s.UseTouchFilter;
        MonitorBox.IsChecked = s.KeyboardReleaseMonitor;
        TouchIsolationBox.IsChecked = s.TouchIsolation;
        if (!_hostEdited) RemoteHostBox.Text = s.RemoteHost;
        _loadingOptions = false;
    }

    public void Refresh()
    {
        var s = Df.Settings;
        var st = Df.Status;
        if (!RemoteHostBox.IsKeyboardFocusWithin && !_hostEdited) RemoteHostBox.Text = s.RemoteHost;

        foreach (var (btn, role) in new[] { (ServerBtn, DeskflowRole.Server), (ClientBtn, DeskflowRole.Client), (OffBtn, DeskflowRole.Off) })
        {
            var selected = s.Role == role;
            btn.Background = Ui.Res(selected ? "AccentSoftBrush" : "Surface3Brush");
            btn.BorderBrush = Ui.Res(selected ? "AccentBrush" : "BorderBrush");
            btn.IsEnabled = !Df.IsSwitching && st.Installed;
        }
        ClientSub.Text = string.IsNullOrWhiteSpace(s.RemoteHost) ? "由另一台电脑控制本机" : "由 " + s.RemoteHost + " 控制本机";

        var tone = !st.Installed ? "err"
            : st.RunningRole != s.Role ? "warn"
            : st.RunningRole == DeskflowRole.Off ? "muted"
            : st.Peers.Count > 0 ? "ok" : "accent";
        var (bg, fg) = tone switch
        {
            "ok" => ("SuccessSoftBrush", "SuccessBrush"),
            "warn" => ("WarnSoftBrush", "WarnBrush"),
            "err" => ("DangerSoftBrush", "DangerBrush"),
            "accent" => ("AccentSoftBrush", "AccentBrush"),
            _ => ("Surface3Brush", "MutedBrush")
        };
        StatusPill.Background = Ui.Res(bg);
        StatusText.Foreground = Ui.Res(fg);
        StatusText.Text = Df.IsSwitching ? "切换中…" : st.Summary;
        if (!string.IsNullOrEmpty(Df.LastAction)) ActionText.Text = Df.LastAction;
        TakeOverBtn.IsEnabled = !s.LegacyTakenOver;
        RestoreBtn.IsEnabled = s.LegacyTakenOver;

        BuildInfo(s, st);

        if ((DateTime.UtcNow - _lastLegacy) > TimeSpan.FromSeconds(30))
        {
            _lastLegacy = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                var text = Df.LegacyStatus();
                Dispatcher.UIThread.Post(() => LegacyText.Text = text);
            });
        }

        if ((DateTime.UtcNow - _lastLog) > TimeSpan.FromSeconds(4))
        {
            _lastLog = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                var text = Df.TailCoreLog(60);
                Dispatcher.UIThread.Post(() =>
                {
                    if (LogBox.Text == text) return;
                    LogBox.Text = text;
                    LogBox.CaretIndex = text.Length;
                });
            });
        }
    }

    private void BuildInfo(DeskflowSettings s, DeskflowStatus st)
    {
        var rows = new List<(string, string)>
        {
            ("期望角色", DeskflowManager.RoleName(s.Role) + (s.Role == DeskflowRole.Client ? " → " + s.RemoteHost : string.Empty)),
            ("实际运行", DeskflowManager.RoleName(st.RunningRole) + (st.CorePid is { } pid ? $"  (deskflow-core pid {pid})" : string.Empty) + (st.MainCores > 1 ? $"  ⚠ {st.MainCores} 个 core" : string.Empty)),
            (st.RunningRole == DeskflowRole.Client ? "已连接服务端" : "已连接客户端", st.Peers.Count == 0 ? "无" : string.Join(", ", st.Peers)),
            ("本机屏幕名 / 端口", $"{st.ScreenName} / {st.Port}{(st.Tls ? "  · TLS" : string.Empty)}"),
            ("对端 Moonlight Hub", !s.LinkPeer ? "联动已关闭"
                : Df.PeerHub is { } peer ? peer.Text
                : Df.PeerHubError.Length > 0 ? Df.PeerHubError : "查询中…"),
            ("本机联动接口", Df.Link.Listening ? $"已开启（端口 {PeerLink.Port}）" : Df.Link.ListenError.Length > 0 ? Df.Link.ListenError : "未开启"),
            ("共享屏幕布局", Df.Layout == null ? (Df.LayoutError.Length > 0 ? Df.LayoutError : "读取中…")
                : Df.LayoutError.Length > 0 ? Df.LayoutError
                : $"{Df.LayoutHost}:{DeskflowPeer.LayoutPort} · 服务端 {Df.Layout.Server} · 屏幕 {string.Join("、", (Df.Layout.Layout["screens"] as System.Text.Json.Nodes.JsonObject)?.Select(kv => kv.Key) ?? Enumerable.Empty<string>())}（在「显示布局」页拖动调整）"),
        };
        if (OperatingSystem.IsWindows())
        {
            rows.Add(("触摸过滤", st.RunningRole != DeskflowRole.Server ? "—（仅服务端）"
                : !st.Filtered ? "未启用"
                : $"已启用 · {(st.FilterHealthy switch { true => "钩子正常", false => "钩子异常", _ => "检查中" })} · F24 规则{(st.F24Rule ? "✓" : "缺失")}"));
            rows.Add(("平板触摸隔离", !s.TouchIsolation ? "已关闭（触摸会移动本机鼠标）"
                : st.RunningRole == DeskflowRole.Server && st.Filtered ? "由服务端的触摸过滤器负责" + (st.TouchBridgeRunning ? "（独立隔离器交接中）" : string.Empty)
                : st.TouchBridgeRunning ? "由 Hub 的触摸隔离器负责（桥接模式）" : "⚠ 隔离器未运行"));
            rows.Add(("触摸释放键盘监视器", st.MonitorRunning ? "运行中" : "未运行"));
            rows.Add(("剪贴板 / 文件 / 布局同步", s.Sidecars.Count == 0 ? "由计划任务运行"
                : string.Join("、", s.Sidecars.Select(x => x.Name.Replace("Deskflow ", string.Empty))) + " · 由 Hub 托管"));
        }
        rows.Add(("运行方式", s.Managed ? "Moonlight Hub 直接运行 deskflow-core" : "未托管（Hub 不启动也不守护 core）"));
        rows.Add(("Deskflow 位置", Df.InstallLocation));
        rows.Add(("配置文件", Df.ConfigPath));

        if (InfoGrid.RowDefinitions.Count != rows.Count)
        {
            InfoGrid.Children.Clear();
            InfoGrid.RowDefinitions.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Ui.T(rows[i].Item1, "Muted");
                label.Margin = new Thickness(0, 0, 0, 6);
                Grid.SetRow(label, i);
                var value = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                InfoGrid.Children.Add(label);
                InfoGrid.Children.Add(value);
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            ((TextBlock)InfoGrid.Children[i * 2]).Text = rows[i].Item1;
            ((TextBlock)InfoGrid.Children[i * 2 + 1]).Text = rows[i].Item2;
        }
    }

    private void Switch(DeskflowRole role, string? host = null)
    {
        ActionText.Text = $"正在切换为{DeskflowManager.RoleName(role)}…" + (Df.Settings.LinkPeer && role != DeskflowRole.Off ? "（同时通知对端）" : string.Empty);
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, msg) = await Df.SetRoleAsync(role, host).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ActionText.Text = msg;
                    _hostEdited = false;
                    Refresh();
                });
                if (!ok) _window.Toast("Deskflow", msg, true);
            }
            catch (Exception ex)
            {
                Log.Error("切换 Deskflow 角色失败", ex);
                _window.Toast("Deskflow", ex.Message, true);
            }
        });
    }

    private void Server_Click(object? sender, RoutedEventArgs e) => Switch(DeskflowRole.Server);

    private void SavePeer_Click(object? sender, RoutedEventArgs e)
    {
        var host = (RemoteHostBox.Text ?? string.Empty).Trim();
        if (!DeskflowPeer.IsValidHost(host))
        {
            _window.Toast("Deskflow", "请填写对端电脑的 IP 地址（例如 10.10.3.136）", true);
            RemoteHostBox.Focus();
            return;
        }
        ActionText.Text = $"正在把 Deskflow 组件改为使用 {host}…";
        _ = Task.Run(() =>
        {
            try
            {
                var notes = Df.SetPeerHost(host);
                Dispatcher.UIThread.Post(() =>
                {
                    _hostEdited = false;
                    ActionText.Text = $"对端地址已保存为 {host}" + (notes.Count > 0 ? "：" + string.Join("；", notes) : "（各组件原本就是这个地址）");
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                Log.Error("保存 Deskflow 对端地址失败", ex);
                _window.Toast("Deskflow", ex.Message, true);
            }
        });
    }

    private void Client_Click(object? sender, RoutedEventArgs e)
    {
        var host = (RemoteHostBox.Text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            _window.Toast("Deskflow", "请先填写对端电脑的 IP（客户端要连接的服务端）", true);
            RemoteHostBox.Focus();
            return;
        }
        Switch(DeskflowRole.Client, host);
    }

    private async void Off_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.Confirm(_window, "停止 Deskflow？本机将不再共享键盘鼠标，直到重新选择服务端或客户端（重启后也保持关闭）。对端不会被切换。", "Deskflow")) return;
        Switch(DeskflowRole.Off);
    }

    private void Reapply_Click(object? sender, RoutedEventArgs e)
    {
        ActionText.Text = "正在重新启动 deskflow-core…";
        _ = Task.Run(async () =>
        {
            var (ok, msg) = await Df.SetRoleAsync(Df.Settings.Role, Df.Settings.RemoteHost, cascade: false).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => { ActionText.Text = msg; Refresh(); });
            if (!ok) _window.Toast("Deskflow", msg, true);
        });
    }

    private void Option_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        var filter = FilterBox.IsChecked == true;
        var filterChanged = filter != Df.Settings.UseTouchFilter;
        Df.UpdateSettings(s =>
        {
            s.Managed = ManagedBox.IsChecked == true;
            s.LinkPeer = LinkBox.IsChecked == true;
            s.UseTouchFilter = filter;
            s.KeyboardReleaseMonitor = MonitorBox.IsChecked == true;
            s.TouchIsolation = TouchIsolationBox.IsChecked == true;
        });
        if (filterChanged && Df.Settings.Role == DeskflowRole.Server) Reapply_Click(sender, e);
    }

    private void TakeOver_Click(object? sender, RoutedEventArgs e)
    {
        ActionText.Text = "正在关闭 Deskflow 自带的开机自启与后台组件…";
        _ = Task.Run(() =>
        {
            var notes = Df.TakeOverLegacy();
            _lastLegacy = DateTime.MinValue;
            Dispatcher.UIThread.Post(() => { ActionText.Text = string.Join("；", notes); LoadOptions(); Refresh(); });
        });
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        var text = OperatingSystem.IsWindows()
            ? "停止 Hub 运行的 deskflow-core，并恢复 Deskflow 服务、Deskflow 界面自启和原来的计划任务？（用于回退到 Hub 接管之前的方式）"
            : "停止 Hub 运行的 deskflow-core，并恢复 Deskflow 界面的开机自启和原来的连接看门狗？";
        if (!await Dialogs.Confirm(_window, text, "回退", danger: true)) return;
        _ = Task.Run(() =>
        {
            var notes = Df.RestoreLegacy();
            _lastLegacy = DateTime.MinValue;
            Dispatcher.UIThread.Post(() => { ActionText.Text = string.Join("；", notes); LoadOptions(); Refresh(); });
        });
    }

    private void OpenConfig_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Path.GetDirectoryName(Df.ConfigPath)!);
    private void OpenLog_Click(object? sender, RoutedEventArgs e) => ProcessUtil.OpenInShell(Df.CoreLogPath);
}
