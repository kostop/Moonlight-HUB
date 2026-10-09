using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MoonlightHub.UI;

public enum DialogResult { None, Yes, No, Cancel }

/// <summary>Themed replacements for WPF's MessageBox (Avalonia has none). All of them are awaitable.</summary>
internal static class Dialogs
{
    /// <summary>OK-only message.</summary>
    public static Task Info(Window? owner, string message, string title, bool warning = false) =>
        Show(owner, message, title, warning, ("确定", DialogResult.Yes, "primary"));

    /// <summary>Yes / No question; true when the user chose yes.</summary>
    public static async Task<bool> Confirm(Window? owner, string message, string title, bool danger = false, string yes = "是", string no = "否") =>
        await Show(owner, message, title, danger, (no, DialogResult.No, "ghost"), (yes, DialogResult.Yes, danger ? "danger" : "primary")) == DialogResult.Yes;

    /// <summary>Yes / No / Cancel question.</summary>
    public static Task<DialogResult> YesNoCancel(Window? owner, string message, string title, string yes = "是", string no = "否", string cancel = "取消") =>
        Show(owner, message, title, false, (cancel, DialogResult.Cancel, "ghost"), (no, DialogResult.No, "ghost"), (yes, DialogResult.Yes, "primary"));

    private static async Task<DialogResult> Show(Window? owner, string message, string title, bool warning, params (string Text, DialogResult Result, string Class)[] buttons)
    {
        var result = DialogResult.Cancel;
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = owner == null || !owner.IsVisible,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner?.Icon != null) window.Icon = owner.Icon;

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (text, value, cls) in buttons)
        {
            var b = new Button { Content = text, MinWidth = 76 };
            b.Classes.Add(cls);
            b.Click += (_, _) => { result = value; window.Close(); };
            buttonRow.Children.Add(b);
        }
        if (buttonRow.Children.LastOrDefault() is Button last) { last.Margin = new Thickness(0); last.IsDefault = true; }
        if (buttons.Any(b => b.Result == DialogResult.Cancel) || buttons.Length == 1)
        {
            foreach (var b in buttonRow.Children.OfType<Button>().Where(b => b.Content as string == buttons.First().Text)) b.IsCancel = true;
        }

        var header = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = warning ? Ui.Res("WarnBrush") : Ui.Res("TextBrush") };
        window.Content = new Border
        {
            Padding = new Thickness(22, 18),
            Child = new StackPanel
            {
                Children =
                {
                    header,
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 20 },
                    buttonRow,
                }
            }
        };

        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            window.Activate();
            await closed.Task;
        }
        return result;
    }
}
