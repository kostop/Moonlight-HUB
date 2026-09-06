using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoonlightHub.Core;
using MoonlightHub.UI.Views;

namespace MoonlightHub.UI;

public partial class MainWindow : Window
{
    private readonly Hub _hub;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, UserControl> _pages = new();
    private bool _reallyClosing;
    private bool _refreshQueued;

    public Hub Hub => _hub;

    public MainWindow(Hub hub)
    {
        _hub = hub;
        InitializeComponent();
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => RefreshAll();
        _hub.Changed += () => QueueRefresh();
        _hub.Engine.Progress += s => Dispatcher.BeginInvoke(() => { ProgressText.Text = s; ProgressStrip.Visibility = Visibility.Visible; });
        _hub.Engine.Applied += () => Dispatcher.BeginInvoke(() => { ProgressStrip.Visibility = Visibility.Collapsed; RefreshAll(); });
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Loaded += (_, _) => { ShowPage("overview"); _timer.Start(); };
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            RefreshAll();
        });
    }

    public void RefreshAll()
    {
        if (!IsVisible) return;
        try
        {
            var vddOk = _hub.Vdd.IsOpen && _hub.Vdd.IsPinging && _hub.Vdd.ConsecutivePingFailures < 3;
            VddDot.Fill = (Brush)FindResource(vddOk ? "SuccessBrush" : (_hub.Vdd.IsOpen ? "WarnBrush" : "MutedBrush"));
            VddText.Text = vddOk ? $"Parsec VDD 保活中 (0.{_hub.Vdd.Version})" : (_hub.Vdd.IsOpen ? "Parsec VDD 保活异常" : "Parsec VDD 未连接");
            var wdOn = _hub.Settings.WatchdogEnabled;
            WatchdogDot.Fill = (Brush)FindResource(wdOn ? "SuccessBrush" : "MutedBrush");
            WatchdogText.Text = wdOn ? "守护运行中" : "守护已关闭";
            ProfileName.Text = _hub.ActiveProfile?.Name ?? "—";
            if (_hub.Engine.IsBusy)
            {
                ProgressStrip.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(_hub.Engine.CurrentStep)) ProgressText.Text = _hub.Engine.CurrentStep;
            }
            else
            {
                ProgressStrip.Visibility = Visibility.Collapsed;
            }
            if (PageHost.Content is IRefreshable page) page.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn("刷新界面失败: " + ex.Message);
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var key = sender switch
        {
            RadioButton rb when rb == NavOverview => "overview",
            RadioButton rb when rb == NavDisplays => "displays",
            RadioButton rb when rb == NavClients => "clients",
            RadioButton rb when rb == NavVirtual => "virtual",
            RadioButton rb when rb == NavTools => "tools",
            RadioButton rb when rb == NavLogs => "logs",
            _ => "settings"
        };
        ShowPage(key);
    }

    public void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "overview" => new OverviewView(this),
                "displays" => new DisplaysView(this),
                "clients" => new ClientsView(this),
                "virtual" => new VirtualDisplaysView(this),
                "tools" => new ToolsView(this),
                "logs" => new LogsView(this),
                _ => new SettingsView(this)
            };
            _pages[key] = page;
        }
        PageTitle.Text = key switch
        {
            "overview" => "概览",
            "displays" => "显示布局",
            "clients" => "客户端 / Sunshine 实例",
            "virtual" => "虚拟屏 (Parsec VDD)",
            "tools" => "工具",
            "logs" => "日志",
            _ => "设置"
        };
        PageHost.Content = page;
        (page as IRefreshable)?.Refresh();
    }

    public void NavigateTo(string key)
    {
        var rb = key switch
        {
            "overview" => NavOverview,
            "displays" => NavDisplays,
            "clients" => NavClients,
            "virtual" => NavVirtual,
            "tools" => NavTools,
            "logs" => NavLogs,
            _ => NavSettings
        };
        rb.IsChecked = true;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        RefreshAll();
    }

    public void RequestExit()
    {
        ShowFromTray();
        var result = MessageBox.Show(this,
            "退出 Moonlight Hub 后，Parsec 虚拟副屏会在几秒内消失（驱动需要上位机保活）。\n\n是否同时停止由它管理的 Sunshine 实例？\n\n是 = 退出并停止实例\n否 = 仅退出（Sunshine 继续运行）\n取消 = 不退出",
            "退出 Moonlight Hub", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return;
        _reallyClosing = true;
        App.Instance.ExitApplication(result == MessageBoxResult.Yes);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClosing && _hub.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_hub.Settings.CloseToTray) Hide();
        else RequestExit();
    }

    public void Toast(string title, string message, bool error = false)
    {
        Dispatcher.BeginInvoke(() => MessageBox.Show(this, message, title, MessageBoxButton.OK, error ? MessageBoxImage.Warning : MessageBoxImage.Information));
    }
}

public interface IRefreshable
{
    void Refresh();
}
