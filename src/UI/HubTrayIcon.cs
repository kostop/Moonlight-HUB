using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

/// <summary>Notification-area icon with quick profile switching (Windows) and Deskflow role switching (both systems).</summary>
public sealed class HubTrayIcon : IDisposable
{
    private readonly Hub _hub;
    private readonly MainWindow _window;
    private readonly Avalonia.Controls.TrayIcon _icon;
    private string _menuSignature = string.Empty;

    public HubTrayIcon(Hub hub, MainWindow window)
    {
        _hub = hub;
        _window = window;
        _icon = new Avalonia.Controls.TrayIcon
        {
            ToolTipText = "Moonlight Hub",
            IsVisible = true,
        };
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MoonlightHub/Assets/app.ico"));
            _icon.Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("加载托盘图标失败: " + ex.Message);
        }
        _icon.Clicked += (_, _) => _window.ShowFromTray();
        Avalonia.Controls.TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
        BuildMenu();
        _hub.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            try { UpdateText(); BuildMenu(); } catch { }
        });
    }

    private void UpdateText()
    {
        var parts = new List<string> { "Moonlight Hub" };
        if (OperatingSystem.IsWindows() && _hub.ActiveProfile != null) parts.Add(_hub.ActiveProfile.Name);
        parts.Add("Deskflow " + DeskflowManager.RoleName(_hub.Deskflow.Settings.Role));
        var text = string.Join(" · ", parts);
        _icon.ToolTipText = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Rebuilds the menu when what it shows changed (native menus cannot refresh on opening everywhere).</summary>
    private void BuildMenu()
    {
        var df = _hub.Deskflow.Settings;
        var standby = OperatingSystem.IsWindows() ? _hub.Standby.Pending : new List<StandbyPrompt>();
        var signature = $"{_hub.ActiveProfile?.Id}|{string.Join(",", _hub.Settings.Profiles.Select(p => p.Id + p.Name))}|{df.Role}|{df.RemoteHost}|{string.Join(",", standby.Select(p => p.InstanceId))}";
        if (signature == _menuSignature) return;
        _menuSignature = signature;

        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开 Moonlight Hub");
        open.Click += (_, _) => _window.ShowFromTray();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());

        if (OperatingSystem.IsWindows())
        {
            var active = _hub.ActiveProfile;
            foreach (var profile in _hub.Settings.Profiles)
            {
                var item = new NativeMenuItem(profile.Name) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = active?.Id == profile.Id };
                var captured = profile;
                item.Click += (_, _) => _ = _hub.Engine.ApplyAsync(captured);
                menu.Items.Add(item);
            }
            menu.Items.Add(new NativeMenuItemSeparator());
        }

        foreach (var prompt in standby)
        {
            var item = new NativeMenuItem($"关闭已断开平板的副屏 #{prompt.Slot + 1}（实例 {prompt.InstanceId}）");
            var captured = prompt;
            item.Click += (_, _) => _ = Task.Run(async () =>
            {
                var (ok, msg) = await _hub.Standby.CloseAsync(captured.InstanceId).ConfigureAwait(false);
                Notify("副屏", msg, !ok);
            });
            menu.Items.Add(item);
        }
        if (standby.Count > 0) menu.Items.Add(new NativeMenuItemSeparator());

        var deskflow = new NativeMenuItem("Deskflow：" + DeskflowManager.RoleName(df.Role)) { Menu = new NativeMenu() };
        foreach (var (role, text) in new[]
                 {
                     (DeskflowRole.Server, "服务端（本机键鼠控制其他电脑）"),
                     (DeskflowRole.Client, string.IsNullOrWhiteSpace(df.RemoteHost) ? "客户端（先在 Deskflow 页填写对端 IP）" : $"客户端（由 {df.RemoteHost} 控制）"),
                     (DeskflowRole.Off, "关闭"),
                 })
        {
            var item = new NativeMenuItem(text)
            {
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = df.Role == role,
                IsEnabled = role != DeskflowRole.Client || !string.IsNullOrWhiteSpace(df.RemoteHost),
            };
            var captured = role;
            item.Click += (_, _) => _ = Task.Run(async () =>
            {
                var (ok, msg) = await _hub.Deskflow.SetRoleAsync(captured).ConfigureAwait(false);
                Notify("Deskflow", msg, !ok);
            });
            deskflow.Menu!.Items.Add(item);
        }
        menu.Items.Add(deskflow);
        menu.Items.Add(new NativeMenuItemSeparator());

        if (OperatingSystem.IsWindows())
        {
            var reapply = new NativeMenuItem("重新应用当前方案");
            reapply.Click += (_, _) => { if (_hub.ActiveProfile != null) _ = _hub.Engine.ApplyAsync(_hub.ActiveProfile); };
            menu.Items.Add(reapply);
        }

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => Dispatcher.UIThread.Post(() => _window.RequestExit());
        menu.Items.Add(exit);
        _icon.Menu = menu;
    }

    /// <summary>Tray icons have no balloons in Avalonia: show the message in the window when it is open, log it otherwise.</summary>
    public void Notify(string title, string text, bool warning = false) => _window.Toast(title, text, warning);

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
