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
// assigned to views through View.SchemeName. No view carries an inline colour, and no second
// palette exists to drift out of sync with ConsoleTheme.
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
        Prompt = new Attribute(ResolveMarkupColor(theme.PromptMarkup, foreground), background, TextStyleFor(theme.PromptMarkup));

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
            Focus = new Attribute(Plain.Foreground, Heading.Foreground),
            HotFocus = new Attribute(Heading.Foreground, Plain.Background),
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