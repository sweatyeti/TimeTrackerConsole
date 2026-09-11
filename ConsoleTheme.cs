using Spectre.Console;

internal sealed class ConsoleTheme
{
    // rendered in the invalid-theme CLI error and in the ArgumentException message
    public const string ValidThemeList = "current, anime";
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
        Color? background)
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

    // console backdrop colour, applied as the terminal's default background for the
    // duration of the session (null = leave the user's terminal background alone)
    public Color? Background { get; }

    // non-throwing lookup. null/blank => default theme, names are case-insensitive,
    // anything else returns false so callers can report a concise CLI error
    // instead of letting an exception escape.
    public static bool TryResolve(string? name, out ConsoleTheme theme)
    {
        string selected = string.IsNullOrWhiteSpace(name) ? DefaultThemeName : name.Trim();

        if(selected.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            theme = new ConsoleTheme("current", Color.DarkOrange, Color.Blue, Color.Green, Color.Red,
                "cyan bold", "orange1 bold", "Chartreuse2", "CadetBlue", "red bold", "green", "blue bold", "red", "blue", "bold", "gray", null);
            return true;
        }

        if(selected.Equals("anime", StringComparison.OrdinalIgnoreCase))
        {
            // deep pine backdrop so the gold/khaki/sky/teal roles sit on their own ground
            theme = new ConsoleTheme("anime", Color.Teal, Color.SkyBlue1, Color.Green3, Color.IndianRed,
                "gold1 bold", "gold1 bold", "khaki1", "skyblue1", "indianred1 bold", "green3", "skyblue1 bold", "indianred1", "skyblue1", "gold1 bold", "skyblue1",
                new Color(0x10, 0x1d, 0x16));
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
