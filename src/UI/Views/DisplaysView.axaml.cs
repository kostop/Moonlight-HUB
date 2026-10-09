using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Common;
using MoonlightHub.Core;
using MoonlightHub.Native;

namespace MoonlightHub.UI.Views;

public partial class DisplaysView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private List<DisplayInfo> _displays = new();
    private string _signature = string.Empty;
    private DateTime _lastEnumerate = DateTime.MinValue;
    private string _peerStatusText = string.Empty;

    public DisplaysView() : this(App.Instance.MainWin) { }

    public DisplaysView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        LayoutCanvas.DraftChanged += () =>
        {
            ApplyLayoutBtn.IsEnabled = LayoutCanvas.HasChanges;
            ResetLayoutBtn.IsEnabled = LayoutCanvas.HasChanges;
        };
        if (!OperatingSystem.IsWindows())
        {
            // Linux: the desktop's own display settings arrange the monitors; here only the other computers are placed
            LayoutCanvas.LockDisplays = true;
            ExtendBtn.IsVisible = ReapplyBtn.IsVisible = false;
            HelpText.Text = "灰色是本机显示器（在系统显示设置里摆放），琥珀色是 Deskflow 的另一台电脑。拖动琥珀色方块到本机显示器的某条边，然后点“应用摆放”：鼠标从那条边移过去就会切过去。";
        }
    }

    private Hub Hub => _window.Hub;

    private List<DisplayInfo> EnumerateLocal() => OperatingSystem.IsWindows() ? DisplayManager.Enumerate() : LinuxDisplays.Enumerate();

    private void ApplyLayout_Click(object? sender, RoutedEventArgs e)
    {
        var draft = LayoutCanvas.DraftPositions;
        if (draft.Count == 0) return;
        var displaysChanged = OperatingSystem.IsWindows() && LayoutCanvas.HasDisplayChanges;
        var peersChanged = LayoutCanvas.HasPeerChanges;
        var peers = LayoutCanvas.DraftPeers;
        var localRects = draft.Select(p => LocalRect.From(p.Display, p.X, p.Y)).ToList();
        var items = draft.Select(p => new LayoutItem(p.Display) { Attached = true, X = p.X, Y = p.Y, Primary = p.Display.Primary }).ToList();
        var host = Hub.Deskflow.LayoutHost;
        var screen = Hub.Deskflow.LocalScreenName;
        var deskflowReady = Hub.Deskflow.Layout?.HasLocalScreen == true && DeskflowPeer.IsValidHost(host);
        DeskflowLayoutText.Text = "正在应用…";
        Ui.RunAsync(_window, async () =>
        {
            var notes = new List<string>();
            if (displaysChanged)
            {
                var r = await Task.Run(() => DisplayManager.ApplyLayout(items)).ConfigureAwait(false);
                Log.Info("手动摆放显示器: " + r);
                if (!r.Success) throw new InvalidOperationException(r.ToString());
                await Task.Delay(800).ConfigureAwait(false);
                Hub.Engine.RememberLayout(DisplayManager.Enumerate(), force: true);
                notes.Add("Windows 显示器位置已应用");
            }
            if (deskflowReady && (displaysChanged || peersChanged))
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // the layout service must know the new monitor topology before peers are placed against it
                        var active = DisplayManager.Enumerate().Where(d => d.Active && d.Width > 0).Select(d => LocalRect.From(d)).ToList();
                        var (changed, _) = await DeskflowPeer.ReportDisplaysAsync(host, screen, active).ConfigureAwait(false);
                        if (changed) notes.Add("已把新的显示器拓扑同步到 Deskflow 共享布局");
                    }
                    if (peersChanged)
                    {
                        await DeskflowPeer.ApplyPeerPositionsAsync(host, screen, localRects, peers).ConfigureAwait(false);
                        notes.Add("Deskflow 屏幕位置已写入共享布局（两台电脑的 Deskflow 会在几秒内按新位置重载）");
                    }
                }
                catch (Exception ex) when (DeskflowPeer.IsUnreachable(ex))
                {
                    // the layout service runs on the Linux side only (gone while the other computer is booted into Windows)
                    if (peersChanged && Hub.Deskflow.CanLinkLocally)
                        notes.Add(await Task.Run(() => Hub.Deskflow.ApplyPeerEdgesLocally(localRects, peers)).ConfigureAwait(false));
                    else if (peersChanged)
                        throw new InvalidOperationException($"布局服务 {host}:{DeskflowPeer.LayoutPort} 不可用（对端可能正运行 Windows 或没开机），本机又不是 Deskflow 服务端，无法修改屏幕位置");
                    else
                        notes.Add($"布局服务 {host}:{DeskflowPeer.LayoutPort} 不可用，新的显示器排列没有同步到共享布局（服务恢复后布局同步会自动上报）");
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LayoutCanvas.ClearPeerDraft();
                DeskflowLayoutText.Text = string.Join("；", notes);
                Invalidate();
            });
            await Hub.Deskflow.RefreshLayoutAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => RefreshPeers(force: true));
        });
    }

    /// <summary>Draws the other Deskflow computers next to the local displays (from the cached shared layout).</summary>
    private void RefreshPeers(bool force = false)
    {
        var df = Hub.Deskflow;
        var active = _displays.Where(d => d.Active && d.Width > 0).Select(d => LocalRect.From(d)).ToList();
        var peers = df.Layout is { } layout ? DeskflowPeer.MapPeers(layout, active) : new List<PeerScreen>();
        LayoutCanvas.SetPeers(peers);
        var text = !df.Status.Installed ? string.Empty
            : df.Layout == null ? (df.LayoutError.Length > 0 ? "Deskflow 共享布局：" + df.LayoutError : "正在读取 Deskflow 共享布局…")
            : df.LayoutError.Length > 0 ? "Deskflow 共享布局：" + df.LayoutError
            : $"Deskflow 共享布局（{df.LayoutHost}:{DeskflowPeer.LayoutPort}）：服务端 {df.Layout.Server}，" +
              (peers.Count == 0 ? "没有其他电脑" : "另一台电脑 " + string.Join("、", peers.Select(p => p.Name))) +
              $"；本机 Deskflow 当前为{DeskflowManager.RoleName(df.Status.RunningRole)}";
        if (force || text != _peerStatusText)
        {
            _peerStatusText = text;
            if (!(DeskflowLayoutText.Text ?? string.Empty).StartsWith("正在应用")) DeskflowLayoutText.Text = text;
        }
    }

    private void ResetLayout_Click(object? sender, RoutedEventArgs e) => LayoutCanvas.ResetDraft();

    public void Refresh()
    {
        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(3))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = EnumerateLocal(); } catch (Exception ex) { Log.Warn("枚举显示器失败: " + ex.Message); }
        }
        if (!LayoutCanvas.HasChanges) LayoutCanvas.SetDisplays(_displays);
        RefreshPeers();
        var sig = string.Join("|", _displays.Select(d => d.ToString()))
                  + (OperatingSystem.IsWindows() ? "#" + ParsecModes.Signature() + "#" + string.Join(",", Hub.State.PendingReplugSlots) : string.Empty);
        if (sig == _signature) return;
        _signature = sig;
        DisplayList.Children.Clear();
        foreach (var d in _displays)
        {
            DisplayList.Children.Add(OperatingSystem.IsWindows() ? BuildRow(d) : BuildLinuxRow(d));
        }
        if (_displays.Count == 0)
        {
            DisplayList.Children.Add(Ui.T("没有找到显示器", "Muted"));
        }
    }

    private Border BuildLinuxRow(DisplayInfo d)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*") };
        var icon = Ui.Icon(Symbol.Desktop, Ui.Res("MutedBrush"), 22);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);
        var info = new StackPanel();
        var head = Ui.Row(new TextBlock { Text = d.GdiName, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (d.Primary) head.Children.Add(Ui.Pill("主屏", "warn"));
        info.Children.Add(head);
        info.Children.Add(Ui.T($"{d.Width}×{d.Height}   位置 ({d.PositionX}, {d.PositionY})", "Muted"));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return Ui.Card(grid, true);
    }

    private Border BuildRow(DisplayInfo d)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto") };

        var icon = Ui.Icon(d.IsParsec ? Symbol.Tablet : Symbol.Desktop, Ui.Res(d.IsParsec ? "AccentBrush" : "MutedBrush"), 22);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var info = new StackPanel();
        var head = Ui.Row(new TextBlock { Text = d.Label, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (d.Primary) head.Children.Add(Ui.Pill("主屏", "warn"));
        head.Children.Add(Ui.Pill(d.Active ? "已激活" : "未激活", d.Active ? "ok" : "muted"));
        if (d.IsParsec) head.Children.Add(Ui.Pill($"虚拟屏 槽位 {d.ParsecSlot}", "accent"));
        if (d.IsParsec && Hub.Engine.IsReplugPending(d.ParsecSlot)) head.Children.Add(Ui.Pill("待重新插拔（客户端断开后）", "warn"));
        info.Children.Add(head);
        info.Children.Add(Ui.T($"{d.GdiName}   {d.ModeText}   位置 {d.PositionText}   {(d.Orientation != 0 ? $"旋转 {d.Orientation * 90}°   " : string.Empty)}", "Muted"));
        var idText = new TextBlock { Text = d.SunshineDeviceId, Foreground = Ui.Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        idText.Classes.Add("mono");
        var idLabel = Ui.T("Sunshine 设备 ID", "Muted");
        idLabel.VerticalAlignment = VerticalAlignment.Center;
        var idRow = Ui.Row(idLabel, idText);
        idRow.Margin = new Thickness(0, 4, 0, 0);
        var copy = Ui.B("复制", async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(d.SunshineDeviceId);
            }
            catch { }
        }, "GhostButton");
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
            if (d.IsParsec) ToolTip.SetTip(combo, "选择的刷新率会记入当前方案（作为下限；跟随客户端帧率/合成刷新率可能再提高）。驱动尚未发布的模式会在客户端断开后自动重新插拔虚拟屏生效。");
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
                var r = DisplayManager.ApplyLayout(new[] { new LayoutItem(d) { Width = d.Width, Height = d.Height, RefreshRate = choice.Hz } });
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
                    var r = DisplayManager.ApplyLayout(new[] { new LayoutItem(d) { Primary = true } });
                    Log.Info($"设为主屏 {d.GdiName}: {r}");
                    Invalidate();
                }, "GhostButton"));
                actions.Children.Add(Ui.B("断开", async (_, _) =>
                {
                    if (!await Dialogs.Confirm(_window, $"确定要把 {d.Label} ({d.GdiName}) 从桌面断开吗？", "断开显示器", danger: true)) return;
                    var r = DisplayManager.ApplyLayout(new[] { new LayoutItem(d) { Attached = false } });
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
                    var combo = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
                    ToolTip.SetTip(combo, "待机中的虚拟屏：选择的刷新率记入方案，客户端连接时由 Sunshine 启用该模式");
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
            Dispatcher.UIThread.Post(Invalidate);
        });
    }

    private void Invalidate()
    {
        _signature = string.Empty;
        _lastEnumerate = DateTime.MinValue;
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => Invalidate();

    private void Extend_Click(object? sender, RoutedEventArgs e)
    {
        var code = DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
        Log.Info($"用户请求扩展全部显示器: SetDisplayConfig={code}");
        Invalidate();
    }

    private void Reapply_Click(object? sender, RoutedEventArgs e)
    {
        var p = Hub.ActiveProfile;
        if (p != null) _ = Hub.Engine.ApplyAsync(p);
    }
}
