using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

internal static class Ui
{
    /// <summary>Theme brush by key (see App.axaml), e.g. "AccentBrush".</summary>
    public static IBrush Res(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return Brushes.Transparent;
    }

    /// <summary>Maps the style names of the WPF version ("Muted", "GhostButton", …) to the Avalonia style classes.</summary>
    public static string ClassFor(string style) => style switch
    {
        "H1" => "h1",
        "H2" => "h2",
        "Muted" => "muted",
        "Mono" => "mono",
        "PrimaryButton" => "primary",
        "GhostButton" => "ghost",
        "DangerButton" => "danger",
        "IconButton" => "icon",
        "Card" => "card",
        "SubCard" => "subcard",
        "Pill" => "pill",
        _ => style
    };

    public static void AddClass(StyledElement element, string? style)
    {
        if (!string.IsNullOrEmpty(style)) element.Classes.Add(ClassFor(style));
    }

    public static TextBlock T(string text, string? style = null, IBrush? fg = null)
    {
        var tb = new TextBlock { Text = text };
        AddClass(tb, style);
        if (fg != null) tb.Foreground = fg;
        return tb;
    }

    /// <summary>Icon for a Segoe MDL2 glyph used by the WPF version (see <see cref="Icons"/>).</summary>
    public static SymbolIcon Icon(string glyph, IBrush? fg = null, double size = 16) => Icon(Icons.FromGlyph(glyph), fg, size);

    public static SymbolIcon Icon(Symbol symbol, IBrush? fg = null, double size = 16)
    {
        var icon = new SymbolIcon { Symbol = symbol, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (fg != null) icon.Foreground = fg;
        return icon;
    }

    public static Button B(string text, EventHandler<RoutedEventArgs> onClick, string? style = null)
    {
        var b = new Button { Content = text };
        AddClass(b, style);
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
        var border = new Border
        {
            Background = Res(bg),
            Child = new TextBlock { Text = text, Foreground = Res(fg), FontSize = 12, FontWeight = FontWeight.SemiBold }
        };
        border.Classes.Add("pill");
        return border;
    }

    public static Border Card(Control child, bool sub = false)
    {
        var border = new Border { Child = child };
        border.Classes.Add(sub ? "subcard" : "card");
        return border;
    }

    public static StackPanel Row(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children)
        {
            if (c.Margin == default) c.Margin = new Thickness(0, 0, 8, 0);
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
