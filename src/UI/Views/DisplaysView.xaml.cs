using System.Windows;
using System.Windows.Controls;
using MoonlightHub.Core;
using MoonlightHub.Native;

namespace MoonlightHub.UI.Views;

public partial class DisplaysView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private List<DisplayInfo> _displays = new();
    private string _signature = string.Empty;
    private DateTime _lastEnumerate = DateTime.MinValue;

    public DisplaysView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        LayoutCanvas.DraftChanged += () =>
        {
            ApplyLayoutBtn.IsEnabled = LayoutCanvas.HasChanges;
            ResetLayoutBtn.IsEnabled = LayoutCanvas.HasChanges;
        };
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        var draft = LayoutCanvas.DraftPositions;
        if (draft.Count == 0) return;
        var items = draft.Select(p => new LayoutItem { GdiName = p.Display.GdiName, Attached = true, X = p.X, Y = p.Y, Primary = p.Display.Primary }).ToList();
        Ui.RunAsync(_window, () => Task.Run(() =>
        {
            var r = DisplayManager.ApplyLayout(items);
            Log.Info("手动摆放显示器: " + r);
            if (!r.Success) throw new InvalidOperationException(r.ToString());
            Thread.Sleep(800);
            Hub.Engine.RememberVirtualPositions(DisplayManager.Enumerate());
            Dispatcher.BeginInvoke(Invalidate);
        }));
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => LayoutCanvas.ResetDraft();

    private Hub Hub => _window.Hub;

    public void Refresh()
    {
        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(3))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = DisplayManager.Enumerate(); } catch (Exception ex) { Log.Warn("枚举显示器失败: " + ex.Message); }
        }
        if (!LayoutCanvas.HasChanges) LayoutCanvas.SetDisplays(_displays);
        var sig = string.Join("|", _displays.Select(d => d.ToString())) + "#" + ParsecModes.Signature() + "#" + string.Join(",", Hub.State.PendingReplugSlots);
        if (sig == _signature) return;
        _signature = sig;
        List.Children.Clear();
        foreach (var d in _displays)
        {
            List.Children.Add(BuildRow(d));
        }
        if (_displays.Count == 0)
        {
            List.Children.Add(Ui.T("没有找到显示器", "Muted"));
        }
    }

    private Border BuildRow(DisplayInfo d)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Ui.Icon(d.IsParsec ? "" : "", Ui.Res(d.IsParsec ? "AccentBrush" : "MutedBrush"), 22);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var info = new StackPanel();
        var head = Ui.Row(new TextBlock { Text = d.Label, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        if (d.Primary) head.Children.Add(Ui.Pill("主屏", "warn"));
        head.Children.Add(Ui.Pill(d.Active ? "已激活" : "未激活", d.Active ? "ok" : "muted"));
        if (d.IsParsec) head.Children.Add(Ui.Pill($"虚拟屏 槽位 {d.ParsecSlot}", "accent"));
        if (d.IsParsec && Hub.Engine.IsReplugPending(d.ParsecSlot)) head.Children.Add(Ui.Pill("待重新插拔（客户端断开后）", "warn"));
        info.Children.Add(head);
        info.Children.Add(Ui.T($"{d.GdiName}   {d.ModeText}   位置 {d.PositionText}   {(d.Orientation != 0 ? $"旋转 {d.Orientation * 90}°   " : string.Empty)}", "Muted"));
        var idRow = Ui.Row(Ui.T("Sunshine 设备 ID", "Muted"), new TextBlock { Text = d.SunshineDeviceId, Style = Ui.St("Mono"), Foreground = Ui.Res("MutedBrush") });
        idRow.Margin = new Thickness(0, 4, 0, 0);
        var copy = Ui.B("复制", (_, _) => { try { Clipboard.SetText(d.SunshineDeviceId); } catch { } }, "GhostButton");
        copy.Padding = new Thickness(8, 2, 8, 2);
        copy.FontSize = 11;
        idRow.Children.Add(copy);
        info.Children.Add(idRow);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (d.Active)
        {
            var modes = DisplayManager.GetModes(d.GdiName, d.Width, d.Height).Where(m => m.Orientation == 0).ToList();
            var choices = modes.Select(m => new ModeChoice(m.RefreshRate, m.ToString(), true)).ToList();
            if (d.IsParsec)
            {
                // the registry is what the driver publishes after the next re-plug; show the difference to the current table
                var registered = ParsecModes.RegisteredRates(d.Width, d.Height);
                foreach (var hz in registered.Where(r => modes.All(m => m.RefreshRate != r)))
                    choices.Add(new ModeChoice(hz, $"{d.Width}×{d.Height} @ {hz}Hz（重新插拔后可用）", false));
                if (registered.Count > 0 && !ParsecModes.IsBuiltInResolution(d.Width, d.Height))
                    foreach (var c in choices.Where(c => c.Exposed && !registered.Contains(c.Hz))) c.Label += "（已从注册表删除）";
            }
            choices = choices.OrderByDescending(c => c.Hz).ToList();
            var combo = new ComboBox { Width = d.IsParsec ? 250 : 170, Margin = new Thickness(0, 0, 8, 0) };
            if (d.IsParsec) combo.ToolTip = "选择的刷新率会记入当前方案（作为下限；跟随客户端帧率/合成刷新率可能再提高）。驱动尚未发布的模式会在客户端断开后自动重新插拔虚拟屏生效。";
            foreach (var c in choices) combo.Items.Add(c);
            combo.SelectedItem = choices.FirstOrDefault(c => c.Hz == d.RefreshRate);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not ModeChoice choice || choice.Hz == d.RefreshRate) return;
                if (d.IsParsec)
                {
                    PickVirtualRate(d.ParsecSlot, choice.Hz);
                    return;
                }
                var r = DisplayManager.ApplyLayout(new[] { new LayoutItem { GdiName = d.GdiName, Width = d.Width, Height = d.Height, RefreshRate = choice.Hz } });
                Log.Info($"手动修改 {d.GdiName} 为 {d.Width}x{d.Height}@{choice.Hz}: {r}");
                _signature = string.Empty;
                _lastEnumerate = DateTime.MinValue;
                Refresh();
            };
            actions.Children.Add(combo);
            if (!d.Primary)
            {
                actions.Children.Add(Ui.B("设为主屏", (_, _) =>
                {
                    var r = DisplayManager.ApplyLayout(new[] { new LayoutItem { GdiName = d.GdiName, Primary = true } });
                    Log.Info($"设为主屏 {d.GdiName}: {r}");
                    Invalidate();
                }, "GhostButton"));
                actions.Children.Add(Ui.B("断开", (_, _) =>
                {
                    if (MessageBox.Show(_window, $"确定要把 {d.Label} ({d.GdiName}) 从桌面断开吗？", "断开显示器", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    var r = DisplayManager.ApplyLayout(new[] { new LayoutItem { GdiName = d.GdiName, Attached = false } });
                    Log.Info($"断开 {d.GdiName}: {r}");
                    Invalidate();
                }, "DangerButton"));
            }
        }
        else
        {
            var profile = Hub.ActiveProfile;
            var spec = d.IsParsec ? profile?.VirtualDisplays.FirstOrDefault(v => v.Slot == d.ParsecSlot) : null;
            if (profile != null && spec != null)
            {
                // stand-by display: offer the registered rates; the choice is stored in the profile and used when a client connects
                var rates = ParsecModes.RegisteredRates(spec.Width, spec.Height);
                if (rates.Count > 0)
                {
                    var combo = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 8, 0), ToolTip = "待机中的虚拟屏：选择的刷新率记入方案，客户端连接时由 Sunshine 启用该模式" };
                    var choices = rates.OrderByDescending(r => r).Select(r => new ModeChoice(r, $"{spec.Width}×{spec.Height} @ {r}Hz", false)).ToList();
                    foreach (var c in choices) combo.Items.Add(c);
                    var target = Hub.Engine.TargetHz(profile, spec, d);
                    combo.SelectedItem = choices.FirstOrDefault(c => c.Hz == target);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is ModeChoice c && c.Hz != target) PickVirtualRate(d.ParsecSlot, c.Hz);
                    };
                    actions.Children.Add(combo);
                }
            }
            actions.Children.Add(Ui.B("激活 (扩展)", (_, _) =>
            {
                var code = DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
                Log.Info($"扩展拓扑: {code}");
                Invalidate();
            }, "GhostButton"));
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return Ui.Card(grid, true);
    }

    private sealed class ModeChoice
    {
        public ModeChoice(int hz, string label, bool exposed) { Hz = hz; Label = label; Exposed = exposed; }
        public int Hz { get; }
        public string Label { get; set; }
        /// <summary>Windows exposes this mode right now (false = registered in the driver table only).</summary>
        public bool Exposed { get; }
        public override string ToString() => Label;
    }

    private void PickVirtualRate(int slot, int hz)
    {
        Ui.RunAsync(_window, async () =>
        {
            var (ok, msg) = await Hub.Engine.SetVirtualDisplayRateAsync(slot, hz).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException(msg);
            _window.Toast("虚拟屏刷新率", msg);
            _ = Dispatcher.BeginInvoke(Invalidate);
        });
    }

    private void Invalidate()
    {
        _signature = string.Empty;
        _lastEnumerate = DateTime.MinValue;
        Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Invalidate();

    private void Extend_Click(object sender, RoutedEventArgs e)
    {
        var code = DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
        Log.Info($"用户请求扩展全部显示器: SetDisplayConfig={code}");
        Invalidate();
    }

    private void Reapply_Click(object sender, RoutedEventArgs e)
    {
        var p = Hub.ActiveProfile;
        if (p != null) _ = Hub.Engine.ApplyAsync(p);
    }
}
