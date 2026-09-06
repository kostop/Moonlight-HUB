using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

internal static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    public static Style St(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock T(string text, string? style = null, Brush? fg = null)
    {
        var tb = new TextBlock { Text = text };
        if (style != null) tb.Style = St(style);
        if (fg != null) tb.Foreground = fg;
        return tb;
    }

    public static TextBlock Icon(string glyph, Brush? fg = null, double size = 16)
    {
        var tb = new TextBlock { Text = glyph, Style = St("Icon"), FontSize = size };
        if (fg != null) tb.Foreground = fg;
        return tb;
    }

    public static Button B(string text, RoutedEventHandler onClick, string? style = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = St(style);
        b.Click += onClick;
        return b;
    }

    public static Border Pill(string text, string tone = "muted")
    {
        var (bg, fg) = tone switch
        {
            "ok" => ("SuccessSoftBrush", "SuccessBrush"),
            "warn" => ("WarnSoftBrush", "WarnBrush"),
            "err" => ("DangerSoftBrush", "DangerBrush"),
            "accent" => ("AccentSoftBrush", "AccentBrush"),
            _ => ("Surface3Brush", "MutedBrush")
        };
        return new Border
        {
            Style = St("Pill"),
            Background = Res(bg),
            Child = new TextBlock { Text = text, Foreground = Res(fg), FontSize = 12, FontWeight = FontWeights.SemiBold }
        };
    }

    public static Border Card(UIElement child, bool sub = false)
    {
        return new Border { Style = St(sub ? "SubCard" : "Card"), Child = child };
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(c);
        }
        return sp;
    }

    public static string InstanceTone(InstanceState state) => state switch
    {
        InstanceState.Streaming => "accent",
        InstanceState.Running => "ok",
        InstanceState.Starting => "warn",
        InstanceState.Unhealthy => "err",
        _ => "muted"
    };

    public static void RunAsync(MainWindow window, Func<Task> work, string? successTitle = null, string? successMessage = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                if (successTitle != null) window.Toast(successTitle, successMessage ?? "完成");
            }
            catch (Exception ex)
            {
                Log.Error("操作失败", ex);
                window.Toast("操作失败", ex.Message, true);
            }
        });
    }
}
