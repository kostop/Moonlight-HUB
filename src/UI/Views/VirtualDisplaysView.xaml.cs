using System.Windows;
using System.Windows.Controls;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class VirtualDisplaysView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private string _signature = string.Empty;
    private string _modeSignature = string.Empty;
    private DateTime _lastEnumerate = DateTime.MinValue;
    private List<DisplayInfo> _displays = new();

    public VirtualDisplaysView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
    }

    private Hub Hub => _window.Hub;

    public void Refresh()
    {
        var vdd = Hub.Vdd;
        var present = ParsecVdd.IsDriverPresent();
        DriverText.Text = !present ? "未检测到 Parsec Virtual Display Adapter（请安装 ParsecVDisplay 附带的驱动）"
            : vdd.IsOpen ? $"已连接 · 驱动版本 0.{vdd.Version} · {vdd.DevicePath}"
            : "驱动已安装，尚未连接 · " + (vdd.LastError ?? string.Empty);
        PingText.Text = vdd.IsOpen
            ? (vdd.IsPinging ? $"keep-alive 运行中 · 间隔 {vdd.PingIntervalMs}ms · 上次 {(vdd.LastPingUtc == default ? "—" : (DateTime.UtcNow - vdd.LastPingUtc).TotalMilliseconds.ToString("0") + "ms 前")} · 连续失败 {vdd.ConsecutivePingFailures}" : "keep-alive 未运行！虚拟屏会被驱动移除")
            : string.Empty;
        OpenBtn.IsEnabled = present && !vdd.IsOpen;

        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(2))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = DisplayManager.Enumerate().Where(d => d.IsParsec).OrderBy(d => d.ParsecSlot).ToList(); } catch { }
        }
        var sig = string.Join("|", _displays.Select(d => d.ToString()));
        if (sig != _signature)
        {
            _signature = sig;
            List.Children.Clear();
            if (_displays.Count == 0) List.Children.Add(Ui.Card(Ui.T("当前没有 Parsec 虚拟屏。选择一个带副屏的方案，或点击「添加虚拟屏」。", "Muted"), true));
            foreach (var d in _displays)
            {
                var grid = new DockPanel();
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var slot = d.ParsecSlot;
                actions.Children.Add(Ui.B("移除", (_, _) =>
                {
                    Ui.RunAsync(_window, () => Task.Run(() => { Hub.Vdd.Open(); Hub.Vdd.RemoveDisplay(slot); Invalidate(); }));
                }, "DangerButton"));
                DockPanel.SetDock(actions, Dock.Right);
                grid.Children.Add(actions);
                var info = new StackPanel();
                var head = Ui.Row(Ui.Icon("", Ui.Res("AccentBrush"), 18), new TextBlock { Text = d.Label, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                head.Children.Add(Ui.Pill(d.Active ? "已激活" : "未激活", d.Active ? "ok" : "warn"));
                if (d.Primary) head.Children.Add(Ui.Pill("主屏", "warn"));
                info.Children.Add(head);
                info.Children.Add(Ui.T($"{d.GdiName} · {d.ModeText} · 位置 {d.PositionText} · UID {d.Uid}", "Muted"));
                info.Children.Add(new TextBlock { Text = "Sunshine ID " + d.SunshineDeviceId, Style = Ui.St("Mono"), Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(0, 2, 0, 0) });
                var mapped = Hub.State.InstanceOutputIds.FirstOrDefault(kv => kv.Value.Equals(d.SunshineDeviceId, StringComparison.OrdinalIgnoreCase));
                if (mapped.Key > 0) info.Children.Add(Ui.T($"由 Sunshine 实例 {mapped.Key} 串流", "Muted"));
                grid.Children.Add(info);
                List.Children.Add(Ui.Card(grid, true));
            }
        }

        var modes = ParsecModes.Read();
        var msig = string.Join("|", modes);
        if (msig != _modeSignature)
        {
            _modeSignature = msig;
            ModesPanel.Children.Clear();
            if (modes.Count == 0) ModesPanel.Children.Add(Ui.T("没有自定义模式", "Muted"));
            foreach (var m in modes)
            {
                var row = Ui.Row(Ui.Pill($"#{m.Index}", "muted"), new TextBlock { Text = m.ToString(), Width = 200, VerticalAlignment = VerticalAlignment.Center });
                var del = Ui.B("删除", (_, _) =>
                {
                    Ui.RunAsync(_window, async () =>
                    {
                        var (ok, msg) = ParsecModes.Remove(m.Index);
                        if (!ok) throw new InvalidOperationException(msg);
                        var note = await Hub.Engine.RefreshModesAsync("删除了自定义模式").ConfigureAwait(false);
                        _window.Toast("自定义模式", $"已删除 {m}。{note}");
                        _ = Dispatcher.BeginInvoke(() => { _modeSignature = string.Empty; Invalidate(); });
                    });
                }, "GhostButton");
                del.Padding = new Thickness(8, 3, 8, 3);
                row.Children.Add(del);
                row.Margin = new Thickness(0, 2, 0, 2);
                ModesPanel.Children.Add(row);
            }
        }
    }

    private void Invalidate()
    {
        _signature = string.Empty;
        _lastEnumerate = DateTime.MinValue;
        Dispatcher.BeginInvoke(Refresh);
    }

    private void RestartAdapter_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(_window, "重启 Parsec Virtual Display Adapter 设备会短暂移除所有虚拟屏，随后守护会自动重新应用当前方案。继续？（会弹出 UAC 提示）", "重启适配器", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Ui.RunAsync(_window, () => Task.Run(() =>
        {
            Hub.Vdd.Close();
            var (ok, msg) = ParsecVdd.RestartAdapter();
            Log.Info("重启 Parsec 适配器: " + msg);
            if (!ok) throw new InvalidOperationException(msg);
            Thread.Sleep(3000);
            if (Hub.Vdd.Open() && Hub.ActiveProfile != null) _ = Hub.Engine.ApplyAsync(Hub.ActiveProfile);
            Invalidate();
        }), "适配器", "已重启，正在重新应用方案");
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Hub.Vdd.Open()) _window.Toast("连接失败", Hub.Vdd.LastError ?? "未知错误", true);
        Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Ui.RunAsync(_window, () => Task.Run(() =>
        {
            if (!Hub.Vdd.Open()) throw new InvalidOperationException(Hub.Vdd.LastError ?? "驱动打开失败");
            var idx = Hub.Vdd.AddDisplay();
            Log.Info($"手动添加虚拟屏，驱动槽位 {idx}");
            DisplayManager.WaitFor(l => l.Any(d => d.IsParsec && d.Uid == 256 + idx && d.Active), TimeSpan.FromSeconds(20));
            Invalidate();
        }));
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(_window, "移除所有 Parsec 虚拟屏？正在使用它们的 Moonlight 会话会丢失画面。", "全部移除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Ui.RunAsync(_window, () => Task.Run(() =>
        {
            if (Hub.Vdd.Open()) Hub.Vdd.RemoveAll();
            Invalidate();
        }));
    }

    private void AddMode_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ModeW.Text, out var w) || !int.TryParse(ModeH.Text, out var h) || !int.TryParse(ModeHz.Text, out var hz) || w < 320 || h < 240 || hz < 24 || hz > 500)
        {
            _window.Toast("参数无效", "请输入有效的宽、高和刷新率", true);
            return;
        }
        Ui.RunAsync(_window, async () =>
        {
            var (ok, msg) = ParsecModes.EnsureRegistered(w, h, hz);
            if (!ok) throw new InvalidOperationException(msg);
            _ = Dispatcher.BeginInvoke(() => { _modeSignature = string.Empty; Refresh(); });
            // the driver only publishes the table for a freshly plugged monitor: re-plug idle displays now, streaming ones later
            var note = await Hub.Engine.RefreshModesAsync("注册了新模式").ConfigureAwait(false);
            _window.Toast("自定义模式", $"已注册 {w}×{h}@{hz}Hz。{note}");
            _ = Dispatcher.BeginInvoke(Invalidate);
        });
    }
}
