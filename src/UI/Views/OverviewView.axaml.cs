using Avalonia.Controls;
using Avalonia.Interactivity;
using MoonlightHub.Core;

namespace MoonlightHub.UI.Views;

public partial class OverviewView : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private List<DisplayInfo> _displays = new();
    private DateTime _lastEnumerate = DateTime.MinValue;

    public OverviewView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private Hub Hub => _window.Hub;

    private void UpdateResponsiveLayout()
    {
        var compact = Bounds.Width < 900;
        StatusTiles.Columns = compact ? 2 : 4;
        OverviewRoot.Height = Math.Max(Bounds.Height, compact ? 860 : 760);
    }

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
        if ((DateTime.UtcNow - _lastEnumerate) > TimeSpan.FromSeconds(3))
        {
            _lastEnumerate = DateTime.UtcNow;
            try { _displays = DisplayManager.Enumerate(); } catch { }
        }
        if (Hub.Deskflow.SharedDisplays is { } shared)
        {
            LayoutCanvas.SetSharedDisplays(shared);
            var standby = shared.Monitors.Count(m => m.Virtual && !m.Active);
            LayoutSummary.Text = $"{shared.Monitors.Count} 块屏幕 · {shared.Monitors.Select(m => m.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 台电脑" +
                (standby > 0 ? $" · {standby} 块虚拟屏待机" : string.Empty);
        }
        else
        {
            LayoutCanvas.SetDisplays(_displays);
            var localRects = _displays.Where(d => d.Active && d.Width > 0).Select(d => LocalRect.From(d)).ToList();
            var peers = Hub.Deskflow.Layout is { } layout ? DeskflowPeer.MapPeers(layout, localRects) : new List<PeerScreen>();
            LayoutCanvas.SetPeers(peers);
            LayoutSummary.Text = $"{localRects.Count} 块本机屏幕" + (peers.Count > 0 ? $" · {peers.Count} 台已连接电脑" : string.Empty);
        }

        var parsecActive = _displays.Count(d => d.IsParsec && d.Active);
        var parsecPresent = _displays.Count(d => d.IsParsec);
        var expected = Hub.State.ExpectedVirtualDisplays;
        var idleOff = Hub.ActiveProfile is { IdleOff: true, Mode: ProfileMode.Extend or ProfileMode.VirtualOnly };
        TileVdd.Text = expected > 0
            ? (idleOff && parsecActive < parsecPresent ? $"{parsecPresent} 待机" : $"{parsecActive} / {expected}")
            : parsecActive.ToString();
        TileVdd.Foreground = Ui.Res(expected > 0 && (idleOff ? parsecPresent : parsecActive) < expected ? "DangerBrush" : "TextBrush");
        TileVddSub.Text = parsecPresent > 0 ? $"已启用 {parsecActive} · 待机 {Math.Max(0, parsecPresent - parsecActive)}" : "当前没有虚拟副屏";
        ToolTip.SetTip(TileVddSub, Hub.Vdd.IsOpen
            ? (Hub.Vdd.IsPinging ? $"驱动 0.{Hub.Vdd.Version} · keep-alive {Hub.Vdd.PingIntervalMs}ms" : "驱动已打开，保活未运行")
            : DriverStatusText());

        var runtimes = Hub.Instances.All.ToList();
        var alive = runtimes.Count(r => r.State is InstanceState.Running or InstanceState.Streaming);
        var streaming = runtimes.Count(r => r.State == InstanceState.Streaming);
        TileInstances.Text = $"{alive} 运行";
        TileInstancesSub.Text = streaming > 0 ? $"{streaming} 个正在串流" : string.Join("  ", runtimes.Where(r => r.Spec.Enabled).Select(r => $"#{r.Spec.Id} {r.StateText}"));

        TileClients.Text = streaming.ToString();
        var addr = Hub.GetHostAddresses().FirstOrDefault();
        TileClientsSub.Text = addr.Address != null ? $"主机地址 {addr.Address}" : "未检测到可用网络";
        ToolTip.SetTip(TileClientsSub, addr.Address != null ? $"{addr.Address} ({addr.Interface})" : "未检测到可用网络");

        var watchdog = Hub.Watchdog.LastSummary;
        TileWatchdog.Text = !Hub.Settings.WatchdogEnabled ? "已关闭" : watchdog.StartsWith("正常") ? "正常" : string.IsNullOrWhiteSpace(watchdog) ? "检查中" : watchdog;
        ToolTip.SetTip(TileWatchdog, watchdog);
        TileWatchdogSub.Text = Hub.Watchdog.LastTickUtc == default ? string.Empty : $"上次检查 {Hub.Watchdog.LastTickUtc.ToLocalTime():HH:mm:ss}";

        var recent = Log.Recent.Where(e => e.Level >= LogLevel.Info).TakeLast(4).Reverse().ToList();
        Activity.ItemsSource = recent;
        ActivityEmpty.IsVisible = recent.Count == 0;
    }

    private void GoDisplays_Click(object? sender, RoutedEventArgs e) => _window.NavigateTo("displays");
    private void GoLogs_Click(object? sender, RoutedEventArgs e) => _window.NavigateTo("logs");
}
