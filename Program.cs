using System.CommandLine;
using System.Linq;
using Spectre.Console;

RootCommand rootCommand = new("A simple console application for tracking time.");

Command newSubCommand = new("new", "Create a new session of times");
Command continueSubCommand = new("continue", "Continue a previous session of times");

rootCommand.Add(newSubCommand);
rootCommand.Add(continueSubCommand);

Option<string?> nameOption = new("--name")
{
    Description = "The name of the session to create."
};
nameOption.Aliases.Add("-n");
newSubCommand.Options.Add(nameOption);

Option<string> themeOption = new("--theme")
{
    Description = "Presentation theme: current (default), anime, or anime-light."
};
themeOption.DefaultValueFactory = _ => ConsoleTheme.DefaultThemeName;
newSubCommand.Options.Add(themeOption);

Option<int> pageSizeOption = new("--page-size")
{
    Description = "Number of menu items (admin options + entries) shown in the main menu before paging. Default: 30."
};
pageSizeOption.DefaultValueFactory = _ => 30;
newSubCommand.Options.Add(pageSizeOption);

Option<bool> tuiOption = new("--tui")
{
    Description = "Use the Terminal.Gui interface instead of the default Spectre.Console one."
};
tuiOption.DefaultValueFactory = _ => false;
newSubCommand.Options.Add(tuiOption);

newSubCommand.SetAction(parseResult => NewSession(
    parseResult.GetValue(nameOption),
    parseResult.GetValue(pageSizeOption),
    parseResult.GetValue(themeOption),
    parseResult.GetValue(tuiOption)
));

Option<int> continuePageSizeOption = new("--page-size")
{
    Description = "Number of menu items (admin options + entries) shown in the main menu before paging. Default: 30."
};
continuePageSizeOption.DefaultValueFactory = _ => 30;
continueSubCommand.Options.Add(continuePageSizeOption);

Option<string> continueThemeOption = new("--theme")
{
    Description = "Presentation theme: current (default), anime, or anime-light."
};
continueThemeOption.DefaultValueFactory = _ => ConsoleTheme.DefaultThemeName;
continueSubCommand.Options.Add(continueThemeOption);

Option<bool> continueTuiOption = new("--tui")
{
    Description = "Use the Terminal.Gui interface instead of the default Spectre.Console one."
};
continueTuiOption.DefaultValueFactory = _ => false;
continueSubCommand.Options.Add(continueTuiOption);

continueSubCommand.SetAction(parseResult => ContinueSession(
    parseResult.GetValue(continuePageSizeOption),
    parseResult.GetValue(continueThemeOption),
    parseResult.GetValue(continueTuiOption)
));

return rootCommand.Parse(args).Invoke();

static void PrintInvalidTheme(string? themeName)
{
    AnsiConsole.MarkupLine($"[red bold]Unknown theme '{Markup.Escape(themeName ?? string.Empty)}'. Valid themes: {ConsoleTheme.ValidThemeList}.[/]");
}

static int NewSession(string? name, int pageSize = 30, string? themeName = null, bool tui = false)
{
    // validate the theme up front: an invalid value must fail before any session
    // state exists, so no entries/*.json file is created by the attempt
    if(!ConsoleTheme.TryResolve(themeName, out ConsoleTheme theme))
    {
        PrintInvalidTheme(themeName);
        return 1;
    }

    // Phase 0: --tui only opens the empty Terminal.Gui shell. Terminal.Gui owns the
    // screen and restores it on exit, so the Spectre path's OSC backdrop is not
    // applied here; no session exists yet, so nothing is written to entries/
    if(tui)
    {
        new TuiSessionWindow().Run();
        return 0;
    }

    // tint the console before the session starts so even the first task prompt sits
    // on it; Restore() runs on every exit path (graceful exit, Ctrl+C, exception)
    ConsoleBackdrop.Apply(theme);
    try
    {
        Session currentSession = Session.StartNew(name, pageSize, theme);

        // call the main session loop that does all the work
        currentSession.MainLoop();

        // the session ended gracefully (StopSession exit path) - stop the background
        // flush loop and force one final write so the on-disk file is up to date,
        // then let the process exit normally
        currentSession.Shutdown();
    }
    finally
    {
        ConsoleBackdrop.Restore();
    }

    return 0;
}

static int ContinueSession(int pageSize = 30, string? themeName = null, bool tui = false)
{
    // same up-front validation as NewSession (no session is touched on bad input)
    if(!ConsoleTheme.TryResolve(themeName, out ConsoleTheme theme))
    {
        PrintInvalidTheme(themeName);
        return 1;
    }

    // Phase 0: --tui only opens the empty Terminal.Gui shell. Listing and resuming a
    // session through Terminal.Gui arrives in a later phase; until then no session is
    // read or written here
    if(tui)
    {
        new TuiSessionWindow().Run();
        return 0;
    }

    List<(SessionSnapshot Snapshot, string FilePath)> sessions = EntryStore.ListAllSessions();

    // tint the console for the whole continue flow (list + resumed session); the
    // finally block restores it on cancel, empty list, and any exit path
    ConsoleBackdrop.Apply(theme);
    try
    {
        if(sessions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.ErrorMarkup}]No previous sessions found.[/]");
            return 0;
        }

        // display a table of available sessions
        Table table = new Table()
            .Title($"[{theme.HeadingMarkup}]Previous Sessions[/]")
            .BorderColor(theme.DetailBorder);
        table.AddColumn("#");
        table.AddColumn("Name");
        table.AddColumn("Started");
        table.AddColumn("Ended");

        for(int i = 0; i < sessions.Count; i++)
        {
            (SessionSnapshot snap, _) = sessions[i];
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(snap.Name ?? "Unnamed session"),
                snap.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                snap.EndedAt is null ? $"[{theme.UnfinishedMarkup}]unfinished[/]" : snap.EndedAt.Value.ToString("yyyy-MM-dd HH:mm"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // let the user pick a session to resume
        SelectionPrompt<int> prompt = new SelectionPrompt<int>()
            .Title("Select a session to resume (press ESC to cancel):")
            .AddChoices(Enumerable.Range(1, sessions.Count))
            .UseConverter(i =>
            {
                (SessionSnapshot snap, _) = sessions[i - 1];
                string status = snap.EndedAt is null ? $" [{theme.UnfinishedMarkup}](unfinished)[/]" : string.Empty;
                return $"{i}. {Markup.Escape(snap.Name ?? "Unnamed session")} - {snap.StartedAt:yyyy-MM-dd HH:mm}{status}";
            });
        prompt.CancelResult = () => 0;

        int choice = prompt.Show(AnsiConsole.Console);
        if(choice == 0) return 0;

        (SessionSnapshot snapshot, string filePath) = sessions[choice - 1];
        Session resumed = Session.Resume(snapshot, filePath, pageSize, theme);
        resumed.MainLoop();
        resumed.Shutdown();

        return 0;
    }
    finally
    {
        ConsoleBackdrop.Restore();
    }
}
