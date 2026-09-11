using Spectre.Console;

internal sealed class ConsoleTheme
{
    // rendered in the invalid-theme CLI error and in the ArgumentException message
    public const string ValidThemeList = "current, anime, anime-light";
    public const string DefaultThemeName = "current";

    private ConsoleTheme(
        string name,
        Color detailBorder,
        Color summaryBorder,
        Color activeColor,
        Color inactiveColor,
        string headingMarkup,
        string promptMarkup,
        string accentMarkup,
        string secondaryMarkup,
        string errorMarkup,
        string positiveMarkup,
        string inProgressMarkup,
        string deletedMarkup,
        string unfinishedMarkup,
        string totalsMarkup,
        string mutedMarkup,
        Color? background,
        Color? foreground)
    {
        Name = name;
        DetailBorder = detailBorder;
        SummaryBorder = summaryBorder;
        ActiveColor = activeColor;
        InactiveColor = inactiveColor;
        HeadingMarkup = headingMarkup;
        PromptMarkup = promptMarkup;
        AccentMarkup = accentMarkup;
        SecondaryMarkup = secondaryMarkup;
        ErrorMarkup = errorMarkup;
        PositiveMarkup = positiveMarkup;
        InProgressMarkup = inProgressMarkup;
        DeletedMarkup = deletedMarkup;
        UnfinishedMarkup = unfinishedMarkup;
        TotalsMarkup = totalsMarkup;
        MutedMarkup = mutedMarkup;
        Background = background;
        Foreground = foreground;
    }

    public string Name { get; }
    public Color DetailBorder { get; }
    public Color SummaryBorder { get; }
    public Color ActiveColor { get; }
    public Color InactiveColor { get; }
    public string HeadingMarkup { get; }
    public string PromptMarkup { get; }
    public string AccentMarkup { get; }
    public string SecondaryMarkup { get; }
    public string ErrorMarkup { get; }
    public string PositiveMarkup { get; }
    public string InProgressMarkup { get; }
    public string DeletedMarkup { get; }
    public string UnfinishedMarkup { get; }

    // labels on the totals line (kept separate from HeadingMarkup so the "current"
    // theme renders exactly as it did before themes existed: bold, default color)
    public string TotalsMarkup { get; }

    // placeholder text such as "No description" (dimmer than SecondaryMarkup;
    // kept separate so the "current" theme still renders it gray)
    public string MutedMarkup { get; }

    // console backdrop colours, applied as the terminal's default colours for the
    // duration of the session (null = leave the user's terminal setting alone).
    // Foreground only needs setting for themes whose ground is light - otherwise the
    // terminal's own (usually light) default text colour would vanish into it.
    public Color? Background { get; }
    public Color? Foreground { get; }

    // non-throwing lookup. null/blank => default theme, names are case-insensitive,
    // anything else returns false so callers can report a concise CLI error
    // instead of letting an exception escape.
    public static bool TryResolve(string? name, out ConsoleTheme theme)
    {
        string selected = string.IsNullOrWhiteSpace(name) ? DefaultThemeName : name.Trim();

        if(selected.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            theme = new ConsoleTheme("current", Color.DarkOrange, Color.Blue, Color.Green, Color.Red,
                "cyan bold", "orange1 bold", "Chartreuse2", "CadetBlue", "red bold", "green", "blue bold", "red", "blue", "bold", "gray",
                null, null);
            return true;
        }

        // anime: vivid pastel-bit roles on a dark pine ground
        if(selected.Equals("anime", StringComparison.OrdinalIgnoreCase))
        {
            theme = new ConsoleTheme("anime", new Color(0x00, 0xd7, 0x87), new Color(0x00, 0xaf, 0xff), new Color(0x00, 0xff, 0x87), new Color(0xff, 0x5f, 0xaf),
                "gold1 bold", "orange1 bold", "khaki1", "lightskyblue1", "red1 bold", "#00d787", "#00afff bold", "#ff5faf", "#00afff", "gold1 bold", "skyblue1",
                new Color(0x16, 0x26, 0x1e), null);
            return true;
        }

        // anime-light: same direction on a bright parchment ground (needs the default
        // foreground set too, since the app's plain text uses it)
        if(selected.Equals("anime-light", StringComparison.OrdinalIgnoreCase))
        {
            theme = new ConsoleTheme("anime-light", new Color(0x2e, 0x8b, 0x57), new Color(0x0b, 0x6b, 0xcb), new Color(0x22, 0x8b, 0x22), new Color(0xc2, 0x18, 0x5b),
                "#0b6b3a bold", "#b45309 bold", "#0f766e", "#3f4a56", "#c81e1e bold", "#15803d", "#0b6bcb bold", "#c2185b", "#0b6bcb", "#0b6b3a bold", "#6b7280",
                new Color(0xf7, 0xf0, 0xdd), new Color(0x2b, 0x32, 0x27));
            return true;
        }

        theme = null!;
        return false;
    }

    // throwing wrapper kept for internal callers that have no user-facing error path
    // (Session.StartNew/Resume fallbacks, whose name is never a CLI-supplied string)
    public static ConsoleTheme Resolve(string? name)
    {
        if(TryResolve(name, out ConsoleTheme theme)) return theme;

        throw new ArgumentException($"Unknown theme '{name}'. Valid themes: {ValidThemeList}.", nameof(name));
    }
}
