using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Common;
using MoonlightHub.Core;
using MoonlightHub.UI.Views;

namespace MoonlightHub.UI;

public partial class MainWindow : Window
{
    private readonly Hub _hub;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, UserControl> _pages = new();
    private WindowNotificationManager? _notifications;
    private bool _reallyClosing;
    private bool _refreshQueued;

    public Hub Hub => _hub;

    /// <summary>The Windows build draws its own title bar; Linux keeps the window manager's decorations.</summary>
    private static bool CustomChrome => OperatingSystem.IsWindows();

    public MainWindow() : this(App.Instance.Hub) { }

    public MainWindow(Hub hub)
    {
        _hub = hub;
        InitializeComponent();
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0") + (OperatingSystem.IsWindows() ? string.Empty : " · Linux");

        if (CustomChrome)
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 52;
        }
        else
        {
            CaptionButtons.IsVisible = false;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Windows-only pages: Sunshine instances, Parsec virtual displays, display profiles, tools
            NavOverview.IsVisible = NavClients.IsVisible = NavVirtual.IsVisible = NavTools.IsVisible = false;
            VddRow.IsVisible = WatchdogRow.IsVisible = ProfilePill.IsVisible = false;
            SubtitleText.Text = "键鼠共享";
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => RefreshAll();
        _hub.Changed += QueueRefresh;
        _hub.Engine.Progress += s => Dispatcher.UIThread.Post(() => { ProgressText.Text = s; ProgressStrip.IsVisible = true; });
        _hub.Engine.Applied += () => Dispatcher.UIThread.Post(() => { ProgressStrip.IsVisible = false; RefreshAll(); });
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) MaxIcon.Symbol = WindowState == WindowState.Maximized ? Symbol.SquareMultiple : Symbol.Maximize;
        };
        Opened += (_, _) =>
        {
            _notifications ??= new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
            if (PageHost.Content == null)
            {
                if (OperatingSystem.IsWindows()) ShowPage("overview");
                else NavDeskflow.IsChecked = true;
            }
            _timer.Start();
        };
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshAll();
        }, DispatcherPriority.Background);
    }

    public void RefreshAll()
    {
        if (!IsVisible) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var vddOk = _hub.Vdd.IsOpen && _hub.Vdd.IsPinging && _hub.Vdd.ConsecutivePingFailures < 3;
                VddDot.Fill = Ui.Res(vddOk ? "SuccessBrush" : (_hub.Vdd.IsOpen ? "WarnBrush" : "MutedBrush"));
                VddText.Text = vddOk ? $"Parsec VDD 保活中 (0.{_hub.Vdd.Version})" : (_hub.Vdd.IsOpen ? "Parsec VDD 保活异常" : "Parsec VDD 未连接");
                var wdOn = _hub.Settings.WatchdogEnabled;
                WatchdogDot.Fill = Ui.Res(wdOn ? "SuccessBrush" : "MutedBrush");
                WatchdogText.Text = wdOn ? "守护运行中" : "守护已关闭";
                ProfileName.Text = _hub.ActiveProfile?.Name ?? "—";
                if (_hub.Engine.IsBusy)
                {
                    ProgressStrip.IsVisible = true;
                    if (!string.IsNullOrEmpty(_hub.Engine.CurrentStep)) ProgressText.Text = _hub.Engine.CurrentStep;
                }
                else
                {
                    ProgressStrip.IsVisible = false;
                }
            }

            var df = _hub.Deskflow.Status;
            var dfWanted = _hub.Deskflow.Settings.Role;
            DeskflowDot.Fill = Ui.Res(!df.Installed ? "MutedBrush"
                : df.RunningRole != dfWanted ? "WarnBrush"
                : df.RunningRole == DeskflowRole.Off ? "MutedBrush"
                : df.Peers.Count > 0 ? "SuccessBrush" : "AccentBrush");
            DeskflowText.Text = df.Installed ? "Deskflow " + df.Summary : "Deskflow 未安装";
            ToolTip.SetTip(DeskflowText, DeskflowText.Text);

            if (PageHost.Content is IRefreshable page) page.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn("刷新界面失败: " + ex.Message);
        }
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        var key = rb == NavOverview ? "overview"
            : rb == NavDisplays ? "displays"
            : rb == NavClients ? "clients"
            : rb == NavVirtual ? "virtual"
            : rb == NavDeskflow ? "deskflow"
            : rb == NavTools ? "tools"
            : rb == NavLogs ? "logs"
            : "settings";
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
                "deskflow" => new DeskflowView(this),
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
            "deskflow" => "键鼠共享 (Deskflow)",
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
            "deskflow" => NavDeskflow,
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

    public async void RequestExit()
    {
        ShowFromTray();
        DialogResult result;
        if (OperatingSystem.IsWindows())
        {
            result = await Dialogs.YesNoCancel(this,
                "退出 Moonlight Hub 后，Parsec 虚拟副屏会在几秒内消失（驱动需要上位机保活）。Deskflow 键鼠共享会继续按当前角色运行。\n\n是否同时停止由它管理的 Sunshine 实例？\n\n是 = 退出并停止实例\n否 = 仅退出（Sunshine 继续运行）\n取消 = 不退出",
                "退出 Moonlight Hub", yes: "退出并停止实例", no: "仅退出");
        }
        else
        {
            result = await Dialogs.Confirm(this, "退出 Moonlight Hub？Deskflow 键鼠共享会继续按当前角色运行，但不再有守护和角色联动。", "退出 Moonlight Hub", yes: "退出")
                ? DialogResult.No : DialogResult.Cancel;
        }
        if (result == DialogResult.Cancel) return;
        _reallyClosing = true;
        App.Instance.ExitApplication(result == DialogResult.Yes);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyClosing && _hub.Settings.CloseToTray && e.CloseReason != WindowCloseReason.ApplicationShutdown && e.CloseReason != WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CustomChrome || e.Source is Button || (e.Source as Visual)?.FindAncestorOfType<Button>() != null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CustomChrome) Maximize_Click(sender, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => Hide();
    private void Maximize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (_hub.Settings.CloseToTray) Hide();
        else RequestExit();
    }

    /// <summary>Non-blocking notification in the window (logged too when the window is hidden in the tray).</summary>
    public void Toast(string title, string message, bool error = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _notifications != null)
            {
                _notifications.Show(new Notification(title, message, error ? NotificationType.Warning : NotificationType.Information, TimeSpan.FromSeconds(error ? 8 : 4)));
            }
            else
            {
                if (error) Log.Warn($"{title}: {message}");
                else Log.Info($"{title}: {message}");
            }
        });
    }
}

public interface IRefreshable
{
    void Refresh();
}
