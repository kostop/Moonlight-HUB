using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class OverviewView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private string _profileSignature = string.Empty;
    private List<DisplayInfo> _displays = new();
    private DateTime _lastEnumerate = DateTime.MinValue;

    public OverviewView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
    }

    private Hub Hub => _window.Hub;

    private DateTime _driverStateUtc = DateTime.MinValue;
    private string _driverStateText = string.Empty;

    /// <summary>Distinguishes "driver not installed" from "device failed, being restarted" (queried at most every 3 s).</summary>
    private string DriverStatusText()
    {
        if ((DateTime.UtcNow - _driverStateUtc) > TimeSpan.FromSeconds(3))
        {
            _driverStateUtc = DateTime.UtcNow;
            try
            {
                var s = ParsecVdd.QueryDeviceState();
                _driverStateText = s.Health switch
                {
                    ParsecVdd.DeviceHealth.Missing => ParsecVdd.HasBundledSetup ? "驱动未安装 · 虚拟屏页可一键安装" : "驱动未安装",
                    ParsecVdd.DeviceHealth.Ok => "驱动未打开",
                    ParsecVdd.DeviceHealth.Disabled => "设备已禁用 · 正在启用",
                    _ => ProcessUtil.IsElevated() ? s.Text + " · 正在自动恢复" : s.Text + " · 需要管理员权限恢复"
                };
            }
            catch { _driverStateText = "驱动状态未知"; }
        }
        return _driverStateText;
    }

    public void Refresh()
    {
        BuildProfiles();

        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(3))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = DisplayManager.Enumerate(); } catch { }
        }
        LayoutCanvas.SetDisplays(_displays);
        if (Hub.Deskflow.Layout is { } deskflowLayout)
        {
            LayoutCanvas.SetPeers(DeskflowPeer.MapPeers(deskflowLayout, _displays.Where(d => d.Active && d.Width > 0).Select(d => LocalRect.From(d)).ToList()));
        }

        var parsecActive = _displays.Count(d => d.IsParsec && d.Active);
        var parsecPresent = _displays.Count(d => d.IsParsec);
        var expected = Hub.State.ExpectedVirtualDisplays;
        var idleOff = Hub.ActiveProfile is { IdleOff: true, Mode: ProfileMode.Extend or ProfileMode.VirtualOnly };
        TileVdd.Text = expected > 0
            ? (idleOff && parsecActive < parsecPresent ? $"{parsecPresent} 待机" : $"{parsecActive} / {expected}")
            : parsecActive.ToString();
        TileVdd.Foreground = Ui.Res(expected > 0 && (idleOff ? parsecPresent : parsecActive) < expected ? "DangerBrush" : "TextBrush");
        TileVddSub.Text = Hub.Vdd.IsOpen
            ? (Hub.Vdd.IsPinging ? $"驱动 0.{Hub.Vdd.Version} · keep-alive {Hub.Vdd.PingIntervalMs}ms" : "驱动已打开，保活未运行")
            : DriverStatusText();

        var runtimes = Hub.Instances.All.ToList();
        var alive = runtimes.Count(r => r.State is InstanceState.Running or InstanceState.Streaming);
        var streaming = runtimes.Count(r => r.State == InstanceState.Streaming);
        TileInstances.Text = $"{alive} 运行";
        TileInstancesSub.Text = streaming > 0 ? $"{streaming} 个正在串流" : string.Join("  ", runtimes.Where(r => r.Spec.Enabled).Select(r => $"#{r.Spec.Id} {r.StateText}"));

        TileClients.Text = streaming.ToString();
        var addr = Hub.GetHostAddresses().FirstOrDefault();
        TileClientsSub.Text = addr.Address != null ? $"主机地址 {addr.Address} ({addr.Interface})" : "未检测到可用网络";

        TileWatchdog.Text = Hub.Settings.WatchdogEnabled ? Hub.Watchdog.LastSummary : "已关闭";
        TileWatchdogSub.Text = Hub.Watchdog.LastTickUtc == default ? string.Empty : $"上次检查 {Hub.Watchdog.LastTickUtc.ToLocalTime():HH:mm:ss}";

        var recent = Log.Recent.Where(e => e.Level >= LogLevel.Info).TakeLast(14).Reverse().ToList();
        Activity.ItemsSource = recent;
    }

    private void BuildProfiles()
    {
        var active = Hub.ActiveProfile?.Id;
        var sig = string.Join("|", Hub.Settings.Profiles.Select(p => p.Id + ":" + p.Name + ":" + p.ModeText)) + "#" + active + "#" + Hub.Engine.IsBusy;
        if (sig == _profileSignature) return;
        _profileSignature = sig;
        ProfilesPanel.Children.Clear();
        foreach (var profile in Hub.Settings.Profiles)
        {
            var isActive = profile.Id == active;
            var card = new Border
            {
                Width = 214,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(16),
                BorderBrush = Ui.Res(isActive ? "AccentBrush" : "BorderBrush"),
                BorderThickness = new Thickness(isActive ? 1.5 : 1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            card.Classes.Add("card");
            if (!string.IsNullOrEmpty(profile.Description)) ToolTip.SetTip(card, profile.Description);
            var stack = new StackPanel();
            var head = new DockPanel();
            var glyph = Ui.Icon(profile.Glyph, Ui.Res(isActive ? "AccentBrush" : "MutedBrush"), 20);
            DockPanel.SetDock(glyph, Dock.Left);
            head.Children.Add(glyph);
            if (isActive)
            {
                var pill = Ui.Pill("使用中", "accent");
                pill.HorizontalAlignment = HorizontalAlignment.Right;
                pill.Margin = new Thickness(0);
                head.Children.Add(pill);
            }
            else
            {
                head.Children.Add(new TextBlock());
            }
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 2) });
            var modeText = Ui.T(profile.ModeText, "Muted");
            modeText.Height = 34;
            stack.Children.Add(modeText);
            var btn = Ui.B(isActive ? "重新应用" : "应用", (_, _) => Apply(profile), isActive ? null : "PrimaryButton");
            btn.IsEnabled = !Hub.Engine.IsBusy;
            btn.Margin = new Thickness(0, 8, 0, 0);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(btn);
            card.Child = stack;
            card.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
                if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) != null) return;
                Apply(profile);
            };
            ProfilesPanel.Children.Add(card);
        }
    }

    private void Apply(Profile profile)
    {
        if (Hub.Engine.IsBusy) return;
        Log.Info($"用户选择方案「{profile.Name}」");
        _ = Hub.Engine.ApplyAsync(profile);
        _profileSignature = string.Empty;
    }

    private void GoDisplays_Click(object? sender, RoutedEventArgs e) => _window.NavigateTo("displays");
    private void GoLogs_Click(object? sender, RoutedEventArgs e) => _window.NavigateTo("logs");
}
