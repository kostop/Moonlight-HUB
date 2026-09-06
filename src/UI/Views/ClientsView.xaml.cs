using System.Windows;
using System.Windows.Controls;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class ClientsView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private readonly Dictionary<int, InstanceCard> _cards = new();
    private List<(string Address, string Interface)> _addresses = new();
    private DateTime _lastAddresses = DateTime.MinValue;

    public ClientsView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
    }

    private Hub Hub => _window.Hub;

    public void Refresh()
    {
        if ((DateTime.UtcNow - _lastAddresses) > TimeSpan.FromSeconds(10))
        {
            _lastAddresses = DateTime.UtcNow;
            _addresses = Hub.GetHostAddresses();
            AddressPanel.Children.Clear();
            var enabled = Hub.Settings.Instances.Where(i => i.Enabled).ToList();
            foreach (var (address, nic) in _addresses)
            {
                var row = Ui.Row(new TextBlock { Text = nic, Style = Ui.St("Muted"), Width = 150 });
                foreach (var inst in enabled)
                {
                    var text = inst.Port == 47989 ? address : $"{address}:{inst.Port}";
                    var pill = Ui.Pill($"实例 {inst.Id}: {text}", inst.Id == 1 ? "accent" : "muted");
                    pill.Cursor = System.Windows.Input.Cursors.Hand;
                    pill.ToolTip = "点击复制";
                    pill.MouseLeftButtonUp += (_, _) => { try { Clipboard.SetText(text); } catch { } };
                    row.Children.Add(pill);
                }
                row.Margin = new Thickness(0, 2, 0, 2);
                AddressPanel.Children.Add(row);
            }
            if (_addresses.Count == 0) AddressPanel.Children.Add(Ui.T("没有检测到可用的 IPv4 地址", "Muted"));
        }

        var runtimes = Hub.Instances.All.ToList();
        foreach (var rt in runtimes)
        {
            if (!_cards.TryGetValue(rt.Spec.Id, out var card))
            {
                card = new InstanceCard(_window, rt.Spec);
                _cards[rt.Spec.Id] = card;
                InstancePanel.Children.Add(card.Root);
            }
            card.Update(rt, Hub.State.InstanceOutputIds.GetValueOrDefault(rt.Spec.Id, string.Empty));
        }
    }

    /// <summary>One instance card: state, capture target, pairing, paired clients and process controls.</summary>
    private sealed class InstanceCard
    {
        private readonly MainWindow _window;
        private readonly InstanceSpec _spec;
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 15 };
        private readonly StackPanel _pills = new() { Orientation = Orientation.Horizontal };
        private readonly TextBlock _detail = new() { Style = Ui.St("Muted") };
        private readonly TextBlock _capture = new() { Style = Ui.St("Muted") };
        private readonly TextBlock _clientInfo = new() { Style = Ui.St("Muted") };
        private readonly TextBlock _pairHint = new() { Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _pinButton;
        private readonly StackPanel _clients = new();
        private readonly TextBlock _credState = new() { Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _startBtn;
        private readonly Button _stopBtn;
        private readonly Button _restartBtn;
        private DateTime _lastClients = DateTime.MinValue;
        private DateTime _lastStreamingProbe = DateTime.MinValue;
        private List<string> _streamingClients = new();
        private bool _loadingClients;
        private bool _revealPassword;

        public Border Root { get; }

        private Hub Hub => _window.Hub;

        public InstanceCard(MainWindow window, InstanceSpec spec)
        {
            _window = window;
            _spec = spec;
            var stack = new StackPanel();

            var head = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            _startBtn = Ui.B("启动", (_, _) => Ui.RunAsync(_window, () => Hub.Instances.StartAsync(_spec)), "PrimaryButton");
            _stopBtn = Ui.B("停止", (_, _) => Ui.RunAsync(_window, () => Hub.Instances.StopAsync(_spec)), "GhostButton");
            _restartBtn = Ui.B("重启", (_, _) => Ui.RunAsync(_window, () => Hub.Instances.RestartAsync(_spec)), "GhostButton");
            actions.Children.Add(_startBtn);
            actions.Children.Add(_stopBtn);
            actions.Children.Add(_restartBtn);
            actions.Children.Add(Ui.B("Web 界面", (_, _) => ProcessUtil.OpenInShell($"https://localhost:{_spec.WebPort}/"), "GhostButton"));
            actions.Children.Add(Ui.B("日志", (_, _) => ProcessUtil.OpenInShell(Hub.Instances.Get(_spec).LogPath), "GhostButton"));
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
            head.Children.Add(Ui.Row(_title, _pills));
            stack.Children.Add(head);
            stack.Children.Add(_detail);
            _capture.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(_capture);
            _clientInfo.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(_clientInfo);

            // pairing
            var pairCard = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            pairCard.Children.Add(Ui.T("配对 Moonlight 客户端", "H2"));
            pairCard.Children.Add(Ui.T("在 Moonlight 里添加这台电脑并点击它，平板会显示 4 位 PIN，同时 Hub 会自动弹出输入框；也可以点下面的按钮手动输入。", "Muted"));
            _pinButton = Ui.B("输入配对 PIN", (_, _) => OpenPairing(), "PrimaryButton");
            var pairRow = Ui.Row(_pinButton, _pairHint);
            pairRow.Margin = new Thickness(0, 8, 0, 0);
            pairCard.Children.Add(pairRow);
            var credRow = Ui.Row(Ui.T("Web 凭据:", "Muted"), _credState,
                Ui.B("显示/隐藏密码", (_, _) => { _revealPassword = !_revealPassword; UpdateCredText(); }, "GhostButton"),
                Ui.B("复制密码", (_, _) => { var p = Hub.Settings.GetPassword(_spec.Id); if (p != null) { try { Clipboard.SetText(p); } catch { } } }, "GhostButton"),
                Ui.B("手动设置…", (_, _) => SetCredentials(), "GhostButton"));
            credRow.Margin = new Thickness(0, 8, 0, 0);
            pairCard.Children.Add(credRow);
            stack.Children.Add(Ui.Card(pairCard, true));

            // paired clients
            var clientsCard = new StackPanel();
            var ch = new DockPanel();
            var reload = Ui.B("刷新列表", (_, _) => LoadClients(true), "GhostButton");
            reload.Margin = new Thickness(0);
            DockPanel.SetDock(reload, Dock.Right);
            ch.Children.Add(reload);
            ch.Children.Add(Ui.T("已配对的客户端", "H2"));
            clientsCard.Children.Add(ch);
            _clients.Margin = new Thickness(0, 8, 0, 0);
            clientsCard.Children.Add(_clients);
            stack.Children.Add(Ui.Card(clientsCard, true));

            Root = Ui.Card(stack);
            Hub.Pairing.PairingFinished += (id, _, ok, _) => { if (id == _spec.Id && ok) _window.Dispatcher.BeginInvoke(() => LoadClients(true)); };
        }

        private void UpdateCredText()
        {
            var user = Hub.Settings.GetUser(_spec.Id);
            if (user == null)
            {
                _credState.Text = Hub.Settings.AutoManageCredentials ? "尚未生成（Hub 会在实例空闲时自动生成）" : "未设置";
                return;
            }
            var pw = Hub.Settings.GetPassword(_spec.Id) ?? string.Empty;
            _credState.Text = $"用户 {user} · 密码 {(_revealPassword ? pw : new string('•', Math.Min(10, pw.Length)))}";
        }

        public void Update(InstanceRuntime rt, string outputId)
        {
            _title.Text = $"实例 {_spec.Id} · {_spec.Name}";
            _pills.Children.Clear();
            _pills.Children.Add(Ui.Pill(rt.StateText, Ui.InstanceTone(rt.State)));
            _pills.Children.Add(Ui.Pill($"端口 {_spec.Port}", "muted"));
            if (_spec.Audio) _pills.Children.Add(Ui.Pill("音频", "muted"));
            var pending = Hub.Pairing.Pending.GetValueOrDefault(_spec.Id);
            if (pending != null && (DateTime.UtcNow - pending.SeenUtc) < TimeSpan.FromMinutes(5)) _pills.Children.Add(Ui.Pill($"等待 PIN · {pending.RemoteAddress}", "warn"));
            _detail.Text = rt.Pid > 0 ? $"pid {rt.Pid} · 监听 {string.Join(",", rt.ListeningPorts.OrderBy(p => p))} · 版本 {rt.Info?.AppVersion}" : (rt.State == InstanceState.Disabled ? "已在设置中停用" : (string.IsNullOrEmpty(rt.LastError) ? "未运行" : "未运行 · " + rt.LastError));
            var target = string.IsNullOrEmpty(outputId) ? "主屏 (未指定 output_name)" : outputId;
            _capture.Text = $"抓取目标: {target} · 配置 {rt.ConfigPath}";

            if (rt.State == InstanceState.Streaming && (DateTime.UtcNow - _lastStreamingProbe) > TimeSpan.FromSeconds(5))
            {
                _lastStreamingProbe = DateTime.UtcNow;
                _streamingClients = Hub.Instances.StreamingClientAddresses(_spec);
            }
            var mode = Hub.State.LastClientMode.GetValueOrDefault(_spec.Id);
            _clientInfo.Text = rt.State == InstanceState.Streaming
                ? $"正在串流 → {(_streamingClients.Count > 0 ? string.Join(", ", _streamingClients) : "客户端")}{(mode != null ? $" · 客户端请求 {mode}" : string.Empty)}"
                : (mode != null ? $"上次客户端请求 {mode}" : string.Empty);

            _startBtn.IsEnabled = !rt.IsAlive && _spec.Enabled;
            _stopBtn.IsEnabled = rt.IsAlive;
            _restartBtn.IsEnabled = rt.IsAlive;
            _pinButton.IsEnabled = rt.IsAlive;
            _pairHint.Text = pending != null ? $"客户端 {pending.RemoteAddress} 正在等待 PIN" : (rt.IsAlive ? "实例运行中，可以配对" : "实例未运行");
            UpdateCredText();
            if (rt.IsAlive && Hub.Settings.GetUser(_spec.Id) != null && (DateTime.UtcNow - _lastClients) > TimeSpan.FromSeconds(30)) LoadClients(false);
            if (!rt.IsAlive && _clients.Children.Count == 0) _clients.Children.Add(Ui.T("实例未运行", "Muted"));
        }

        private void OpenPairing()
        {
            var pending = Hub.Pairing.Pending.GetValueOrDefault(_spec.Id) ?? new PairingRequest(_spec.Id, "手动输入", string.Empty, DateTime.UtcNow);
            App.Instance.ShowPairingWindow(pending);
        }

        private void LoadClients(bool force)
        {
            if (_loadingClients) return;
            if (!force && (DateTime.UtcNow - _lastClients) < TimeSpan.FromSeconds(30)) return;
            _loadingClients = true;
            _lastClients = DateTime.UtcNow;
            var api = Hub.Instances.Api(_spec);
            _ = Task.Run(async () =>
            {
                var list = await api.ListClientsAsync().ConfigureAwait(false);
                await _window.Dispatcher.InvokeAsync(() =>
                {
                    _clients.Children.Clear();
                    if (list.Count == 0)
                    {
                        _clients.Children.Add(Ui.T(Hub.Settings.GetUser(_spec.Id) == null ? "需要 Web 凭据才能读取列表" : "还没有配对的客户端", "Muted"));
                    }
                    foreach (var c in list)
                    {
                        var row = Ui.Row(Ui.Icon("", Ui.Res("MutedBrush")), new TextBlock { Text = c.Name, Width = 220 }, new TextBlock { Text = c.Uuid, Style = Ui.St("Mono"), Foreground = Ui.Res("MutedBrush"), Width = 300 });
                        var unpair = Ui.B("取消配对", (_, _) =>
                        {
                            if (MessageBox.Show(_window, $"取消与「{c.Name}」的配对？", "取消配对", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                            Ui.RunAsync(_window, async () =>
                            {
                                var (ok, msg) = await api.UnpairAsync(c.Uuid).ConfigureAwait(false);
                                Log.Info($"实例 {_spec.Id} 取消配对 {c.Name}: {msg}");
                                await _window.Dispatcher.InvokeAsync(() => LoadClients(true));
                                if (!ok) throw new InvalidOperationException(msg);
                            });
                        }, "DangerButton");
                        unpair.Padding = new Thickness(8, 3, 8, 3);
                        row.Children.Add(unpair);
                        row.Margin = new Thickness(0, 2, 0, 2);
                        _clients.Children.Add(row);
                    }
                    _loadingClients = false;
                });
            });
        }

        private void SetCredentials()
        {
            var dlg = new CredentialsDialog(_spec, Hub.Settings.GetUser(_spec.Id)) { Owner = _window };
            if (dlg.ShowDialog() != true) return;
            if (dlg.ResetOnSunshine)
            {
                Ui.RunAsync(_window, async () =>
                {
                    var (ok, msg) = await Hub.Instances.SetCredentialsAsync(_spec, dlg.User, dlg.Password).ConfigureAwait(false);
                    if (!ok) throw new InvalidOperationException(msg);
                }, "凭据", "已在 Sunshine 中重置并保存凭据");
            }
            else
            {
                Hub.Settings.SetCredential(_spec.Id, dlg.User, dlg.Password);
                Hub.Settings.Save();
                Ui.RunAsync(_window, async () =>
                {
                    var (ok, msg) = await Hub.Instances.Api(_spec).TestAuthAsync().ConfigureAwait(false);
                    if (!ok) throw new InvalidOperationException("凭据测试失败: " + msg);
                }, "凭据", "凭据有效并已保存");
            }
        }
    }
}
