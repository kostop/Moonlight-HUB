using System.Drawing;
using System.IO;
using System.Windows;
using MoonlightHub.Core;
using WinForms = System.Windows.Forms;

namespace MoonlightHub.UI;

/// <summary>Notification-area icon with quick profile switching.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Hub _hub;
    private readonly MainWindow _window;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu = new();

    public TrayIcon(Hub hub, MainWindow window)
    {
        _hub = hub;
        _window = window;
        _icon = new WinForms.NotifyIcon
        {
            Text = "Moonlight Hub 副屏中心",
            Visible = true,
            Icon = LoadIcon(),
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _window.ShowFromTray());
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _hub.Changed += () => { try { UpdateText(); } catch { } };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var info = Application.GetResourceStream(uri);
            if (info != null)
            {
                using var stream = info.Stream;
                return new Icon(stream, 32, 32);
            }
        }
        catch { }
        var path = Path.Combine(Paths.ExeDir, "MoonlightHub.exe");
        return (File.Exists(path) ? Icon.ExtractAssociatedIcon(path) : null) ?? SystemIcons.Application;
    }

    private void UpdateText()
    {
        var profile = _hub.ActiveProfile;
        var text = "Moonlight Hub" + (profile != null ? " · " + profile.Name : string.Empty);
        if (text.Length > 63) text = text[..63];
        _icon.Text = text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new WinForms.ToolStripMenuItem("打开 Moonlight Hub");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _window.ShowFromTray());
        _menu.Items.Add(open);
        _menu.Items.Add(new WinForms.ToolStripSeparator());

        var active = _hub.ActiveProfile;
        foreach (var profile in _hub.Settings.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(profile.Name) { Checked = active?.Id == profile.Id };
            var captured = profile;
            item.Click += (_, _) => _ = _hub.Engine.ApplyAsync(captured);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new WinForms.ToolStripSeparator());

        var reapply = new WinForms.ToolStripMenuItem("重新应用当前方案");
        reapply.Click += (_, _) => { if (_hub.ActiveProfile != null) _ = _hub.Engine.ApplyAsync(_hub.ActiveProfile); };
        _menu.Items.Add(reapply);

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _window.RequestExit());
        _menu.Items.Add(exit);
    }

    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        try { _icon.ShowBalloonTip(4000, title, text, icon); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
