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
    private bool _scaleOperation;
    private bool _layoutOperation;
    private bool _refreshOperation;
    private bool _displayError;
    private const string DisplayBusyText = "正在调整显示器，完成后可继续摆放屏幕…";

    public DisplaysView() : this(App.Instance.MainWin) { }

    public DisplaysView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        LayoutCanvas.DraftChanged += UpdateDisplayControls;
        LayoutCanvas.ScaleRequested += ApplyMonitorScale;
        if (!OperatingSystem.IsWindows())
        {
            // Linux: the desktop's own display settings arrange the monitors; here only the other computers are placed
            LayoutCanvas.LockDisplays = true;
            ExtendBtn.IsVisible = ReapplyBtn.IsVisible = false;
            PointerCard.IsVisible = false;
            HelpText.Text = "灰色是本机显示器（在系统显示设置里摆放），琥珀色是 Deskflow 的另一台电脑。拖动琥珀色方块到本机显示器的某条边，然后点“应用摆放”：鼠标从那条边移过去就会切过去。";
        }
    }

    private Hub Hub => _window.Hub;

    private bool DisplayBusy => _scaleOperation || _layoutOperation || Hub.Engine.IsBusy || Hub.Deskflow.PeerHub?.DisplayBusy == true;

    private void UpdateDisplayControls()
    {
        var busy = DisplayBusy;
        ApplyLayoutBtn.IsEnabled = ResetLayoutBtn.IsEnabled = !busy && LayoutCanvas.HasChanges;
        ExtendBtn.IsEnabled = ReapplyBtn.IsEnabled = !busy;
        LayoutCanvas.IsEnabled = !busy;
        if (busy && !_scaleOperation && !_layoutOperation && !_displayError)
            DeskflowLayoutText.Text = DisplayBusyText;
        else if (!busy && DeskflowLayoutText.Text == DisplayBusyText)
        {
            DeskflowLayoutText.Text = string.Empty;
            RefreshPeers(force: true);
        }
    }

    private List<DisplayInfo> EnumerateLocal() => OperatingSystem.IsWindows() ? DisplayManager.Enumerate() : LinuxDisplays.Enumerate();

    private void ApplyLayout_Click(object? sender, RoutedEventArgs e)
    {
        if (DisplayBusy) { UpdateDisplayControls(); return; }
        if (LayoutCanvas.SharedDocument != null)
        {
            var positions = LayoutCanvas.DraftSharedPositions;
            if (positions.Count == 0) return;
            var leaderHost = Hub.Deskflow.SharedDisplayServerHost;
            _layoutOperation = true;
            _displayError = false;
            DeskflowLayoutText.Text = "正在同步两台电脑的显示器布局…";
            UpdateDisplayControls();
            Ui.RunAsync(_window, async () =>
            {
                try
                {
                    var applied = await ApplySharedPositionsAsync(leaderHost, positions).ConfigureAwait(false);
                    await Hub.Deskflow.RefreshSharedDisplaysAsync().ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LayoutCanvas.ClearPeerDraft(); LayoutCanvas.SetSharedDisplays(applied);
                        Invalidate(); Refresh();
                        DeskflowLayoutText.Text = "两台电脑的显示器布局已同步";
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "应用失败：" + ex.Message; });
                    throw;
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { _layoutOperation = false; UpdateDisplayControls(); });
                }
            });
            return;
        }
        var draft = LayoutCanvas.DraftPositions;
        var gone = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // a display detached or unplugged since the canvas was drawn is left out: Windows refuses a layout naming it
            var active = DisplayManager.Enumerate().Where(d => d.Active && d.Width > 0).Select(d => d.GdiName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            gone = draft.Where(p => !active.Contains(p.Display.GdiName)).Select(p => p.Display.GdiName).ToList();
            draft = draft.Where(p => active.Contains(p.Display.GdiName)).ToList();
            if (gone.Count > 0) Invalidate();
        }
        if (draft.Count == 0) return;
        var displaysChanged = OperatingSystem.IsWindows() && draft.Any(p => p.X != p.Display.PositionX || p.Y != p.Display.PositionY);
        var peersChanged = LayoutCanvas.HasPeerChanges;
        var peers = LayoutCanvas.DraftPeers;
        var localRects = draft.Select(p => LocalRect.From(p.Display, p.X, p.Y)).ToList();
        var items = draft.Select(p => new LayoutItem(p.Display) { Attached = true, X = p.X, Y = p.Y, Primary = p.Display.Primary }).ToList();
        var host = Hub.Deskflow.LayoutHost;
        var screen = Hub.Deskflow.LocalScreenName;
        var deskflowReady = Hub.Deskflow.Layout?.HasLocalScreen == true && DeskflowPeer.IsValidHost(host);
        _layoutOperation = true;
        _displayError = false;
        DeskflowLayoutText.Text = "正在应用…";
        UpdateDisplayControls();
        Ui.RunAsync(_window, async () =>
        {
            var notes = new List<string>();
            if (gone.Count > 0) notes.Add("已忽略已断开的 " + string.Join("、", gone));
            try
            {
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
            }
            catch (Exception ex)
            {
                // the status line said "正在应用…" for good after a refused layout
                await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "应用失败：" + ex.Message; });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _layoutOperation = false; UpdateDisplayControls(); });
            }
        });
    }

    /// <param name="host">当前共同布局的服务端地址。</param>
    /// <param name="positions">点击应用时保存的拖动位置，未拖动的显示器不覆盖。</param>
    private static async Task<SharedDisplayDocument> ApplySharedPositionsAsync(string host, List<(string Owner, string Id, int X, int Y)> positions)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fresh = await PeerLink.GetSharedDisplaysAsync(host).ConfigureAwait(false);
            var draft = SharedDisplayLayout.Clone(fresh);
            foreach (var position in positions)
            {
                var index = draft.Monitors.FindIndex(m => m.Owner == position.Owner && m.Id == position.Id);
                if (index < 0) throw new InvalidOperationException($"{position.Owner} 的显示器已断开或变化，请刷新布局");
                draft.Monitors[index] = draft.Monitors[index] with { X = position.X, Y = position.Y };
            }
            try { return await PeerLink.ApplySharedDisplaysAsync(host, draft).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (attempt == 0 && ex.Message == "布局已更新，请刷新后再应用")
            {
                // 缩放或另一台电脑刚更新了布局：重新读取一次，只重放用户拖动的位置。
            }
        }
    }

    /// <summary>Draws the other Deskflow computers next to the local displays (from the cached shared layout).</summary>
    private void RefreshPeers(bool force = false)
    {
        var df = Hub.Deskflow;
        if (df.SharedDisplays is { } shared)
        {
            if (!_displayError && !(DeskflowLayoutText.Text ?? "").StartsWith("正在"))
                DeskflowLayoutText.Text = $"两台电脑共享布局 · {shared.Monitors.Count} 块屏幕 · 服务端 {shared.Server}";
            return;
        }
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
            if (!_displayError && !(DeskflowLayoutText.Text ?? string.Empty).StartsWith("正在")) DeskflowLayoutText.Text = text;
        }
    }

    private void ResetLayout_Click(object? sender, RoutedEventArgs e)
    {
        if (!DisplayBusy) LayoutCanvas.ResetDraft();
    }

    public void Refresh()
    {
        UpdateDisplayControls();
        if (OperatingSystem.IsWindows() && Hub.Deskflow.SharedDisplays is { } shared)
        {
            LayoutCanvas.SetSharedDisplays(shared);
            HelpText.Text = "同色代表同一台电脑。每块屏幕独立拖动并在同色屏幕之间吸附，不能重叠；重新连接后保留位置。虚拟屏可拖动右下角调节缩放，也可右键选择比例，分辨率保持原值。";
        }
        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(3))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = EnumerateLocal(); } catch (Exception ex) { Log.Warn("枚举显示器失败: " + ex.Message); }
        }
        LayoutCanvas.SetDisplays(_displays); // keeps the drafts of displays that are still there
        RefreshPeers();
        if (OperatingSystem.IsWindows()) RefreshPointer();
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

    // ------------------------------------------------------------------ pointer speed per screen

    /// <param name="monitor">本机或对端被调整的虚拟屏。</param>
    /// <param name="percent">选择或拖动产生的真实显示缩放百分比。</param>
    private void ApplyMonitorScale(SharedMonitor monitor, int percent)
    {
        if (DisplayBusy)
        {
            LayoutCanvas.ResetSizePreview(); UpdateDisplayControls();
            return;
        }
        var host = monitor.Owner == Hub.Deskflow.LocalScreenName ? "127.0.0.1" : Hub.Deskflow.Settings.RemoteHost;
        _scaleOperation = true;
        _displayError = false;
        DeskflowLayoutText.Text = $"正在应用 {monitor.Owner} 的虚拟屏缩放 {percent}%…";
        UpdateDisplayControls();
        Ui.RunAsync(_window, async () =>
        {
            try
            {
                var message = await PeerLink.SetContentScaleAsync(host, monitor.VirtualSlot, percent).ConfigureAwait(false);
                await Hub.Deskflow.RefreshSharedDisplaysAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (Hub.Deskflow.SharedDisplays is { } document) LayoutCanvas.RebaseSharedDisplays(document);
                    Refresh(); DeskflowLayoutText.Text = message; _window.Toast("缩放比例", message);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "缩放失败：" + ex.Message; });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _scaleOperation = false; LayoutCanvas.ResetSizePreview(); UpdateDisplayControls();
                });
            }
        });
    }

    private string _pointerSignature = string.Empty;
    private bool _buildingPointer;

    private void RefreshPointer()
    {
        var ps = Hub.PointerSpeed;
        var enabled = Hub.Settings.PointerSpeedEnabled;
        var screens = ps.Screens;
        var sig = $"{enabled}|{ps.Baseline}|{ps.CurrentKey}|" + string.Join(";", screens.Select(s => $"{s.Key},{s.Label},{s.Scale:0.00},{s.AutoSpeed},{s.ManualSpeed}"));
        if (sig == _pointerSignature) return;
        _pointerSignature = sig;
        _buildingPointer = true;
        try
        {
            PointerToggle.IsChecked = enabled;
            PointerList.Children.Clear();
            PointerList.Children.Add(Ui.T($"Windows 里设置的鼠标速度：{ps.Baseline}（作为主物理屏的速度；没有激活物理屏时按 100% 缩放的屏计算）", "Muted"));
            if (screens.Count == 0)
            {
                PointerList.Children.Add(Ui.T(enabled ? "正在读取屏幕…" : "已关闭：始终使用 Windows 里设置的速度", "Muted"));
                return;
            }
            foreach (var s in screens)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
                var info = new StackPanel();
                var head = Ui.Row(new TextBlock { Text = s.IsPeer ? $"{s.Label}（Deskflow 另一台电脑）" : s.Label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (enabled && s.Key == ps.CurrentKey) head.Children.Add(Ui.Pill("光标在这里 · 速度 " + ps.CurrentSpeed, "ok"));
                info.Children.Add(head);
                info.Children.Add(Ui.T($"缩放 {s.Scale * 100:0}%{(s.IsPeer && Hub.Deskflow.PeerHub?.DpiScale is not > 0 ? "（对端未报告，按 100%）" : string.Empty)} · 自动速度 {s.AutoSpeed}", "Muted"));
                grid.Children.Add(info);

                var combo = new ComboBox { Width = 150, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
                combo.Items.Add($"自动（{s.AutoSpeed}）");
                for (var i = 1; i <= 20; i++) combo.Items.Add(i.ToString());
                combo.SelectedIndex = s.ManualSpeed ?? 0;
                var key = s.Key;
                combo.SelectionChanged += (_, _) =>
                {
                    if (_buildingPointer || combo.SelectedIndex < 0) return;
                    Hub.PointerSpeed.SetManual(key, combo.SelectedIndex == 0 ? null : combo.SelectedIndex);
                    Log.Info($"鼠标速度: {key} → {(combo.SelectedIndex == 0 ? "自动" : combo.SelectedIndex.ToString())}");
                };
                Grid.SetColumn(combo, 1);
                grid.Children.Add(combo);
                PointerList.Children.Add(grid);
            }
        }
        finally
        {
            _buildingPointer = false;
        }
    }

    private void PointerToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_buildingPointer) return;
        Hub.Settings.PointerSpeedEnabled = PointerToggle.IsChecked == true;
        Hub.Settings.Save();
        Hub.PointerSpeed.Invalidate();
        Log.Info("按屏幕切换鼠标速度: " + (Hub.Settings.PointerSpeedEnabled ? "开" : "关"));
        _pointerSignature = string.Empty;
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
            var profile = Hub.ActiveProfile;
            var spec = d.IsParsec ? profile?.VirtualDisplays.FirstOrDefault(v => v.Slot == d.ParsecSlot) : null;
            var targetWidth = d.Width;
            var targetHeight = d.Height;
            var targetHz = d.RefreshRate;
            List<VirtualModeChoice> choices;
            if (d.IsParsec && profile != null && spec != null)
            {
                targetWidth = spec.Width;
                targetHeight = spec.Height;
                targetHz = Hub.Engine.TargetHz(profile, spec, d);
                // 同时列出驱动已发布的模式、注册表里等待重新插拔的自定义模式，以及方案当前模式。
                choices = ParsecModeCatalog.Build(d.GdiName, targetWidth, targetHeight, targetHz);
            }
            else
            {
                choices = DisplayManager.GetModes(d.GdiName, d.Width, d.Height)
                    .Where(m => m.Orientation == 0)
                    .Select(m => new VirtualModeChoice(d.Width, d.Height, m.RefreshRate, true))
                    .OrderByDescending(c => c.Hz)
                    .ToList();
            }
            var combo = new ComboBox { Width = d.IsParsec ? 290 : 170, Margin = new Thickness(0, 0, 8, 0) };
            if (d.IsParsec)
                ToolTip.SetTip(combo, "选择完整的分辨率和刷新率会记入当前方案并立即应用；注册表中尚未发布的模式会在客户端断开后自动重新插拔虚拟屏生效。手动选择分辨率会关闭当前方案的“跟随客户端分辨率”。");
            foreach (var c in choices) combo.Items.Add(c);
            combo.SelectedItem = choices.FirstOrDefault(c => c.Width == targetWidth && c.Height == targetHeight && c.Hz == targetHz)
                ?? choices.FirstOrDefault(c => c.Width == d.Width && c.Height == d.Height && c.Hz == d.RefreshRate);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not VirtualModeChoice choice) return;
                if (d.IsParsec)
                {
                    if (choice.Width == targetWidth && choice.Height == targetHeight && choice.Hz == targetHz) return;
                    PickVirtualMode(d.ParsecSlot, choice.Width, choice.Height, choice.Hz);
                    return;
                }
                if (choice.Hz == d.RefreshRate) return;
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
                // 待机虚拟屏无法枚举 Windows 模式，但仍可从注册表自定义模式中选择完整分辨率。
                var targetHz = Hub.Engine.TargetHz(profile, spec, d);
                var choices = ParsecModeCatalog.Build(null, spec.Width, spec.Height, targetHz);
                var combo = new ComboBox { Width = 290, Margin = new Thickness(0, 0, 8, 0) };
                ToolTip.SetTip(combo, "待机中的虚拟屏：选择的分辨率和刷新率记入方案，客户端连接时由 Sunshine 启用该模式");
                foreach (var c in choices) combo.Items.Add(c);
                combo.SelectedItem = choices.FirstOrDefault(c => c.Width == spec.Width && c.Height == spec.Height && c.Hz == targetHz);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not VirtualModeChoice c) return;
                    if (c.Width == spec.Width && c.Height == spec.Height && c.Hz == targetHz) return;
                    PickVirtualMode(d.ParsecSlot, c.Width, c.Height, c.Hz);
                };
                actions.Children.Add(combo);
            }
            actions.Children.Add(Ui.B("激活 (扩展)", (_, _) =>
            {
                var all = DisplayManager.Enumerate();
                var target = all.FirstOrDefault(x => !string.IsNullOrEmpty(d.SunshineDeviceId) &&
                        x.SunshineDeviceId.Equals(d.SunshineDeviceId, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(x => x.DevicePath.Equals(d.DevicePath, StringComparison.OrdinalIgnoreCase))
                    ?? d;
                var code = DisplayManager.ActivateDisplays(new[] { target }, all.Where(x => x.Active).ToList());
                Log.Info($"显式激活显示器 {d.Label} ({d.GdiName}): {code}");
                if (code != 0)
                {
                    var extend = DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND);
                    Log.Info($"显式激活失败，扩展拓扑: {extend}");
                    _window.Toast("激活显示器", $"{d.Label} 激活失败 ({code})；如果笔记本合盖，请先打开盖子", true);
                }
                else
                {
                    _window.Toast("激活显示器", $"已请求启用 {d.Label}");
                }
                Invalidate();
            }, "GhostButton"));
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return Ui.Card(grid, true);
    }

    /// <summary>用户从显示布局选择虚拟屏的完整模式。</summary>
    /// <param name="slot">虚拟屏槽位，从零开始。</param>
    /// <param name="width">目标宽度（像素）。</param>
    /// <param name="height">目标高度（像素）。</param>
    /// <param name="hz">目标刷新率（Hz）。</param>
    private void PickVirtualMode(int slot, int width, int height, int hz)
    {
        Ui.RunAsync(_window, async () =>
        {
            var (ok, msg) = await Hub.Engine.SetVirtualDisplayModeAsync(slot, width, height, hz).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException(msg);
            _window.Toast("虚拟屏模式", msg);
            Dispatcher.UIThread.Post(Invalidate);
        });
    }

    private void Invalidate()
    {
        _signature = string.Empty;
        _lastEnumerate = DateTime.MinValue;
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (LayoutCanvas.SharedDocument == null) { Invalidate(); return; }
        if (_refreshOperation) return;
        _refreshOperation = true;
        var host = Hub.Deskflow.SharedDisplayServerHost;
        Ui.RunAsync(_window, async () =>
        {
            try
            {
                var fresh = await PeerLink.GetSharedDisplaysAsync(host).ConfigureAwait(false);
                await Hub.Deskflow.RefreshSharedDisplaysAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _displayError = false; LayoutCanvas.RebaseSharedDisplays(fresh);
                    Invalidate(); Refresh();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "刷新失败：" + ex.Message; });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _refreshOperation = false; UpdateDisplayControls(); });
            }
        });
    }

    private void Extend_Click(object? sender, RoutedEventArgs e)
    {
        if (DisplayBusy) { UpdateDisplayControls(); return; }
        _layoutOperation = true; _displayError = false; UpdateDisplayControls();
        Ui.RunAsync(_window, async () =>
        {
            try
            {
                var code = await Task.Run(() => DisplayManager.SetTopology(NativeMethods.SDC_TOPOLOGY_EXTEND)).ConfigureAwait(false);
                Log.Info($"用户请求扩展全部显示器: SetDisplayConfig={code}");
                if (code != 0) throw new InvalidOperationException($"扩展显示器失败（{code}）");
                await Dispatcher.UIThread.InvokeAsync(Invalidate);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "扩展失败：" + ex.Message; });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _layoutOperation = false; UpdateDisplayControls(); });
            }
        });
    }

    private void Reapply_Click(object? sender, RoutedEventArgs e)
    {
        if (DisplayBusy) { UpdateDisplayControls(); return; }
        var p = Hub.ActiveProfile;
        if (p == null) return;
        _layoutOperation = true; _displayError = false; UpdateDisplayControls();
        Ui.RunAsync(_window, async () =>
        {
            try
            {
                var result = await Hub.Engine.ApplyAsync(p).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(result.Summary);
                await Hub.Deskflow.RefreshSharedDisplaysAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(Invalidate);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _displayError = true; DeskflowLayoutText.Text = "应用方案失败：" + ex.Message; });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { _layoutOperation = false; UpdateDisplayControls(); });
            }
        });
    }
}
