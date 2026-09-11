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
    Description = "Presentation theme: current (default) or anime."
};
themeOption.DefaultValueFactory = _ => ConsoleTheme.DefaultThemeName;
newSubCommand.Options.Add(themeOption);

Option<int> pageSizeOption = new("--page-size")
{
    Description = "Number of menu items (admin options + entries) shown in the main menu before paging. Default: 30."
};
pageSizeOption.DefaultValueFactory = _ => 30;
newSubCommand.Options.Add(pageSizeOption);

newSubCommand.SetAction(parseResult => NewSession(
    parseResult.GetValue(nameOption),
    parseResult.GetValue(pageSizeOption),
    parseResult.GetValue(themeOption)
));

Option<int> continuePageSizeOption = new("--page-size")
{
    Description = "Number of menu items (admin options + entries) shown in the main menu before paging. Default: 30."
};
continuePageSizeOption.DefaultValueFactory = _ => 30;
continueSubCommand.Options.Add(continuePageSizeOption);

Option<string> continueThemeOption = new("--theme")
{
    Description = "Presentation theme: current (default) or anime."
};
continueThemeOption.DefaultValueFactory = _ => ConsoleTheme.DefaultThemeName;
continueSubCommand.Options.Add(continueThemeOption);

continueSubCommand.SetAction(parseResult => ContinueSession(
    parseResult.GetValue(continuePageSizeOption),
    parseResult.GetValue(continueThemeOption)
));

return rootCommand.Parse(args).Invoke();

static void PrintInvalidTheme(string? themeName)
{
    AnsiConsole.MarkupLine($"[red bold]Unknown theme '{Markup.Escape(themeName ?? string.Empty)}'. Valid themes: {ConsoleTheme.ValidThemeList}.[/]");
}

static int NewSession(string? name, int pageSize = 30, string? themeName = null)
{
    // validate the theme up front: an invalid value must fail before any session
    // state exists, so no entries/*.json file is created by the attempt
    if(!ConsoleTheme.TryResolve(themeName, out ConsoleTheme theme))
    {
        PrintInvalidTheme(themeName);
        return 1;
    }

    Session currentSession = Session.StartNew(name, pageSize, theme);

    // call the main session loop that does all the work
    currentSession.MainLoop();

    // the session ended gracefully (StopSession exit path) - stop the background
    // flush loop and force one final write so the on-disk file is up to date,
    // then let the process exit normally
    currentSession.Shutdown();

    return 0;
}

static int ContinueSession(int pageSize = 30, string? themeName = null)
{
    // same up-front validation as NewSession (no session is touched on bad input)
    if(!ConsoleTheme.TryResolve(themeName, out ConsoleTheme theme))
    {
        PrintInvalidTheme(themeName);
        return 1;
    }

    List<(SessionSnapshot Snapshot, string FilePath)> sessions = EntryStore.ListAllSessions();
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
