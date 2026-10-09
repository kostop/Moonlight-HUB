using FluentIcons.Common;

namespace MoonlightHub.UI;

/// <summary>
/// The WPF version drew icons with Segoe MDL2 glyphs, a font that only exists on Windows. Fluent System Icons are
/// bundled with the app instead; this maps the glyphs still stored in settings (profile icons) and used in code.
/// </summary>
internal static class Icons
{
    public static Symbol FromGlyph(string? glyph) => glyph switch
    {
        "" => Symbol.Home,
        "" => Symbol.Desktop,
        "" => Symbol.PhoneLaptop,
        "" => Symbol.Tablet,
        "" => Symbol.Keyboard,
        "" => Symbol.Wrench,
        "" => Symbol.DocumentText,
        "" => Symbol.Settings,
        "" => Symbol.Subtract,
        "" => Symbol.Maximize,
        "" => Symbol.SquareMultiple,
        "" => Symbol.Dismiss,
        "" => Symbol.ArrowSync,
        "" => Symbol.Grid,
        "" => Symbol.Copy,
        "" => Symbol.Star,
        "" => Symbol.Power,
        "" => Symbol.ChevronDown,
        _ => Symbol.Desktop
    };
}
