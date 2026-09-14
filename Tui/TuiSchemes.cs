using System;
using System.Collections.Generic;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using SpectreColor = Spectre.Console.Color;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Phase 3 (T3.1/T3.2) of the Terminal.Gui migration: the ONE projection of the ConsoleTheme
// role table into Terminal.Gui schemes.
//
// This replaces Phase 1's TuiPalette stopgap. The roles are still read from ConsoleTheme - the
// same table the Spectre path renders from, so a theme change is a data change in one file - but
// they are now built into named Scheme objects that are registered with SchemeManager and
// assigned to views through View.SchemeName. No view carries an inline colour.
//
// One exception, added deliberately: a theme may also carry a TuiPalette (see ConsoleTheme.cs)
// whose roles take precedence here. The two paths are different rendering surfaces and can
// legitimately want different colours under one theme name - that is why the TUI's default
// palette is a warm charcoal while the Spectre path keeps the vivid one. Every theme except
// "current" carries no TuiPalette, so for them nothing changed: the roles come from ConsoleTheme
// exactly as described above.
//
// Mapping (migration plan section 6):
//   HeadingMarkup   -> HotNormal            (and the summary header scheme)
//   AccentMarkup    -> Focus / HotFocus / Highlight
//   PromptMarkup    -> the "prompt" scheme used by dialog instruction labels
//   SecondaryMarkup -> ReadOnly
//   ActiveColor     -> Active               (and the ACTIVE banner)
//   InactiveColor   -> Disabled             (and the NOT ACTIVE banner / unlogged emphasis)
//   PositiveMarkup  -> Normal of the plain row scheme, overridden per row by the row renderer
//   DeletedMarkup / UnfinishedMarkup -> Highlight
//
// Deviation, deliberate and recorded: section 6 maps PromptMarkup to Normal. Normal is the role
// Terminal.Gui uses for *all* unstyled text, and the Spectre path renders entry text unstyled
// there - so Normal has to stay the terminal/theme default foreground or every entry row would
// come out in the prompt colour. PromptMarkup colours exactly one thing on the Spectre path, the
// "Select an option or entry" instruction line, so it maps to the prompt scheme the dialogs'
// instruction labels use instead.
internal sealed class TuiSchemes
{
    private const string NamePrefix = "ttc";

    // SchemeManager.AddScheme updates an existing name rather than refusing it, and the scheme
    // names are theme-prefixed, so one registration per theme name per process is enough and two
    // themes can never fight over one name.
    private static readonly HashSet<string> RegisteredNames = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    private readonly Scheme _summaryPlainScheme;
    private readonly Scheme _summaryUnloggedScheme;

    public TuiSchemes(ConsoleTheme theme)
    {
        ThemeName = theme.Name;

        // A theme may carry a TUI-only palette (see TuiPalette). Every role below prefers it and
        // falls back to the ConsoleTheme field, which is what the Spectre path still renders.
        TuiPalette? tui = theme.Tui;

        // the terminal's own defaults when the theme does not pin a background
        Color defaultForeground = Attribute.Default.Foreground;
        Color defaultBackground = Attribute.Default.Background;

        Color background = (tui?.Background ?? theme.Background) is { } bg ? ToGui(bg) : defaultBackground;
        Color foreground = (tui?.Foreground ?? theme.Foreground) is { } fg ? ToGui(fg) : defaultForeground;

        Plain = new Attribute(foreground, background);
        Background = background;

        Active = ToGui(tui?.ActiveColor ?? theme.ActiveColor);
        Inactive = ToGui(tui?.InactiveColor ?? theme.InactiveColor);

        string headingMarkup = tui?.HeadingMarkup ?? theme.HeadingMarkup;
        string totalsMarkup = tui?.TotalsMarkup ?? theme.TotalsMarkup;
        string accentMarkup = tui?.AccentMarkup ?? theme.AccentMarkup;
        string secondaryMarkup = tui?.SecondaryMarkup ?? theme.SecondaryMarkup;
        string mutedMarkup = tui?.MutedMarkup ?? theme.MutedMarkup;
        string positiveMarkup = tui?.PositiveMarkup ?? theme.PositiveMarkup;
        string inProgressMarkup = tui?.InProgressMarkup ?? theme.InProgressMarkup;
        string promptMarkup = tui?.PromptMarkup ?? theme.PromptMarkup;

        Heading = new Attribute(ResolveMarkupColor(headingMarkup, foreground), background, TextStyleFor(headingMarkup));
        Totals = new Attribute(ResolveMarkupColor(totalsMarkup, foreground), background, TextStyleFor(totalsMarkup));
        Accent = new Attribute(ResolveMarkupColor(accentMarkup, foreground), background, TextStyleFor(accentMarkup));
        Secondary = new Attribute(ResolveMarkupColor(secondaryMarkup, foreground), background, TextStyleFor(secondaryMarkup));
        Muted = new Attribute(ResolveMarkupColor(mutedMarkup, foreground), background, TextStyleFor(mutedMarkup));
        Positive = new Attribute(ResolveMarkupColor(positiveMarkup, foreground), background, TextStyleFor(positiveMarkup));
        InProgress = new Attribute(ResolveMarkupColor(inProgressMarkup, foreground), background, TextStyleFor(inProgressMarkup));
        Prompt = new Attribute(ResolveMarkupColor(promptMarkup, foreground), background, TextStyleFor(promptMarkup));

        // the summary's "unlogged minutes" emphasis and the ACTIVE banner use the theme's
        // inactive/active colours, exactly as the Spectre path does
        Unlogged = new Attribute(Inactive, background);
        BannerActive = new Attribute(Active, background);
        BannerInactive = new Attribute(Inactive, background);

        string prefix = $"{NamePrefix}-{theme.Name}";
        BaseName = $"{prefix}-base";
        BannerActiveName = $"{prefix}-banner-active";
        BannerInactiveName = $"{prefix}-banner-inactive";
        SummaryHeaderName = $"{prefix}-summary-header";
        SummaryUnloggedName = $"{prefix}-summary-unlogged";
        TotalsName = $"{prefix}-totals";
        PromptName = $"{prefix}-prompt";

        // the base scheme: every role comes from the theme, so a themed TUI run never falls
        // back to Terminal.Gui's own blue default
        Scheme baseScheme = new()
        {
            Normal = Plain,
            HotNormal = Heading,
            // the focus/selection fill is the theme's heading colour, so the text on top of it
            // has to be picked for legibility against THAT fill. Using the plain foreground put
            // white on "current"'s cyan heading and made the selected row unreadable.
            Focus = new Attribute(ReadableOn(Heading.Foreground), Heading.Foreground),
            HotFocus = new Attribute(ReadableOn(Accent.Foreground), Accent.Foreground),
            Active = new Attribute(Active, Background),
            Disabled = new Attribute(Inactive, Background),
            Highlight = new Attribute(Accent.Foreground, Background),
            Editable = Plain,
            ReadOnly = Secondary
        };

        _summaryPlainScheme = baseScheme;
        _summaryUnloggedScheme = new Scheme(baseScheme) { Normal = Unlogged, Disabled = Unlogged };

        SummaryHeaderScheme = new Scheme(baseScheme) { Normal = Heading };
        BannerActiveScheme = new Scheme(baseScheme) { Normal = BannerActive, Active = BannerActive };
        BannerInactiveScheme = new Scheme(baseScheme) { Normal = BannerInactive, Active = BannerInactive, Disabled = BannerInactive };
        TotalsScheme = new Scheme(baseScheme) { Normal = Totals };
        PromptScheme = new Scheme(baseScheme) { Normal = Prompt };

        Register(BaseName, baseScheme);
        Register(BannerActiveName, BannerActiveScheme);
        Register(BannerInactiveName, BannerInactiveScheme);
        Register(SummaryHeaderName, SummaryHeaderScheme);
        Register(SummaryUnloggedName, _summaryUnloggedScheme);
        Register(TotalsName, TotalsScheme);
        Register(PromptName, PromptScheme);
    }

    public string ThemeName { get; }

    // registered scheme names, assigned to views through View.SchemeName
    public string BaseName { get; }
    public string BannerActiveName { get; }
    public string BannerInactiveName { get; }
    public string SummaryHeaderName { get; }
    public string SummaryUnloggedName { get; }
    public string TotalsName { get; }
    public string PromptName { get; }

    // the same schemes as objects: TableStyle.HeaderScheme and ITableSource.RowColorGetter
    // take Scheme instances rather than names
    public Scheme SummaryHeaderScheme { get; }
    public Scheme BannerActiveScheme { get; }
    public Scheme BannerInactiveScheme { get; }
    public Scheme TotalsScheme { get; }
    public Scheme PromptScheme { get; }

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
    public Attribute Prompt { get; }
    public Attribute Unlogged { get; }
    public Attribute BannerActive { get; }
    public Attribute BannerInactive { get; }

    // the per-row scheme for the summary table: a named task that still has unlogged minutes
    // gets the unlogged emphasis, everything else stays on the base scheme
    public Scheme SummaryRowScheme(bool highlightUnlogged) => highlightUnlogged ? _summaryUnloggedScheme : _summaryPlainScheme;

    private static void Register(string name, Scheme scheme)
    {
        lock(Gate)
        {
            if(!RegisteredNames.Add(name)) return;
        }

        SchemeManager.AddScheme(name, scheme);
    }

    private static Color ToGui(SpectreColor color) => new(color.R, color.G, color.B);

    // Picks black or white for text drawn on `background`, by relative luminance (the WCAG
    // formula). The focus/selection blocks are filled with a theme accent colour, so the
    // readable foreground depends on that fill, not on the theme's normal text colour - the
    // previous code assumed the latter and put white on "current"'s cyan heading.
    private static Color ReadableOn(Color background)
    {
        static double Linear(int channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        double luminance = (0.2126 * Linear(background.R))
                         + (0.7152 * Linear(background.G))
                         + (0.0722 * Linear(background.B));

        return luminance > 0.4 ? new Color(0, 0, 0) : new Color(255, 255, 255);
    }

    // "gold1 bold" / "#00afff bold" -> the colour token; the bold keyword is carried into the
    // attribute's TextStyle instead of the colour lookup
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