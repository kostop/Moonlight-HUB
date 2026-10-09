using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

/// <summary>
/// "The tablet disconnected — keep the virtual display?" Shown on top of everything; closes by itself when the tablet
/// reconnects (see <see cref="StandbyPrompter"/>).
/// </summary>
public partial class StandbyPromptWindow : Window
{
    private readonly Hub? _hub;
    private readonly StandbyPrompt? _prompt;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public int InstanceId => _prompt?.InstanceId ?? 0;

    public StandbyPromptWindow() => InitializeComponent();

    public StandbyPromptWindow(Hub hub, StandbyPrompt prompt) : this()
    {
        _hub = hub;
        _prompt = prompt;
        HeaderText.Text = $"平板的 Moonlight 已断开（实例 {prompt.InstanceId}）";
        BodyText.Text = $"虚拟副屏 #{prompt.Slot + 1} 和上面的窗口都还保留着。如果只是不小心断开，重新连上就能继续用（连上后这个提示会自动关闭）。";
        _timer.Tick += (_, _) => UpdateElapsed();
        UpdateElapsed();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateElapsed()
    {
        if (_prompt == null) return;
        var elapsed = DateTime.UtcNow - _prompt.DisconnectedUtc;
        ElapsedText.Text = elapsed.TotalSeconds < 5 ? "刚刚断开" : $"已断开 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>Closes the prompt because the question resolved itself (tablet back, session ended…).</summary>
    public void Withdraw(string reason)
    {
        Log.Info("关闭副屏确认窗口: " + reason);
        Close();
    }

    private void Keep_Click(object? sender, RoutedEventArgs e)
    {
        if (_prompt != null) _hub?.Standby.Keep(_prompt.InstanceId);
        Close();
    }

    private async void CloseDisplay_Click(object? sender, RoutedEventArgs e)
    {
        if (_hub == null || _prompt == null) { Close(); return; }
        CloseBtn.IsEnabled = KeepBtn.IsEnabled = false;
        CloseBtn.Content = "正在关闭…";
        var (ok, msg) = await Task.Run(() => _hub.Standby.CloseAsync(_prompt.InstanceId));
        if (!ok)
        {
            await Dialogs.Info(this, msg, "关闭副屏", warning: true);
        }
        Close();
    }
}
