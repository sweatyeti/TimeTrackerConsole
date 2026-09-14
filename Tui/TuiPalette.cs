using System;
using Terminal.Gui.Drawing;
using SpectreColor = Spectre.Console.Color;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Phase 1 of the Terminal.Gui migration: turns the ConsoleTheme role table into
// Terminal.Gui colours/attributes so the read-only view can colour from the SAME
// role table the Spectre path uses (no second palette to keep in sync).
//
// Phase 3 (Tui/TuiSchemes.cs per the migration plan) replaces this with named,
// registered schemes assigned by SchemeName - this class deliberately stays a plain
// projection kept in one place, not a scheme registry.
internal sealed class TuiPalette
{
    public TuiPalette(ConsoleTheme theme)
    {
        // the terminal's own defaults when the theme does not pin a background
        // (the "current" theme leaves the console colours alone)
        Color defaultForeground = Attribute.Default.Foreground;
        Color defaultBackground = Attribute.Default.Background;

        Color background = theme.Background is { } bg ? ToGui(bg) : defaultBackground;
        Color foreground = theme.Foreground is { } fg ? ToGui(fg) : defaultForeground;

        Plain = new Attribute(foreground, background);
        Background = background;

        Active = ToGui(theme.ActiveColor);
        Inactive = ToGui(theme.InactiveColor);

        Heading = new Attribute(ResolveMarkupColor(theme.HeadingMarkup, foreground), background, TextStyleFor(theme.HeadingMarkup));
        Totals = new Attribute(ResolveMarkupColor(theme.TotalsMarkup, foreground), background, TextStyleFor(theme.TotalsMarkup));
        Accent = new Attribute(ResolveMarkupColor(theme.AccentMarkup, foreground), background, TextStyleFor(theme.AccentMarkup));
        Secondary = new Attribute(ResolveMarkupColor(theme.SecondaryMarkup, foreground), background, TextStyleFor(theme.SecondaryMarkup));
        Muted = new Attribute(ResolveMarkupColor(theme.MutedMarkup, foreground), background, TextStyleFor(theme.MutedMarkup));
        Positive = new Attribute(ResolveMarkupColor(theme.PositiveMarkup, foreground), background, TextStyleFor(theme.PositiveMarkup));
        InProgress = new Attribute(ResolveMarkupColor(theme.InProgressMarkup, foreground), background, TextStyleFor(theme.InProgressMarkup));

        // the summary's "unlogged minutes" emphasis (red for a named task with unlogged
        // work) and the ACTIVE banner use the theme's inactive/accent colours
        Unlogged = new Attribute(ToGui(theme.InactiveColor), background);
        BannerActive = new Attribute(Active, background);
        BannerInactive = new Attribute(Inactive, background);
    }

    public Attribute Plain { get; }
    public Color Background { get; }
    public Color Active { get; }
    public Color Inactive { get; }
    public Attribute Heading { get; }
    public Attribute Totals { get; }
    public Attribute Accent { get; }
    public Attribute Secondary { get; }
    public Attribute Muted { get; }
    public Attribute Positive { get; }
    public Attribute InProgress { get; }
    public Attribute Unlogged { get; }
    public Attribute BannerActive { get; }
    public Attribute BannerInactive { get; }

    // the window/list/table base scheme: every role comes from the theme so a themed
    // TUI run never falls back to Terminal.Gui's own blue default
    public Scheme BaseScheme => new()
    {
        Normal = Plain,
        HotNormal = Heading,
        Focus = new Attribute(Plain.Foreground, Heading.Foreground),
        HotFocus = new Attribute(Heading.Foreground, Plain.Background),
        Active = new Attribute(Active, Background),
        Disabled = new Attribute(Inactive, Background),
        Highlight = new Attribute(Accent.Foreground, Background),
        Editable = Plain,
        ReadOnly = Secondary
    };

    // the row scheme handed back by SummaryTableSource.RowColorGetter for a named task
    // that still has unlogged minutes (replaces Spectre's red markup cell)
    public Scheme UnloggedScheme()
    {
        Scheme scheme = BaseScheme;
        return new Scheme(scheme) { Normal = Unlogged };
    }

    private static Color ToGui(SpectreColor color) => new(color.R, color.G, color.B);

    // "gold1 bold" / "#00afff bold" -> the colour token; the bold keyword is carried
    // into the attribute's TextStyle instead of the colour lookup
    private static Color ResolveMarkupColor(string markup, Color fallback)
    {
        string token = markup.Trim().Split(' ')[0];
        if(token.StartsWith("#", StringComparison.Ordinal) && SpectreColor.TryFromHex(token, out SpectreColor hex))
        {
            return ToGui(hex);
        }

        SpectreColor? named = SpectreColor.FromName(token);
        return named.HasValue ? ToGui(named.Value) : fallback;
    }

    private static TextStyle TextStyleFor(string markup)
        => markup.Contains("bold", StringComparison.Ordinal) ? TextStyle.Bold : TextStyle.None;
}
