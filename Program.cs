using System.CommandLine;

RootCommand rootCommand = new("A simple console application for tracking time.");

Command newSubCommand = new("new", "Create a new session of times");
//Command continueSubCommand = new("continue", "Continue the previous session of times");
// add one for displaying a previous session of times
// add one for editing a previous session of times

rootCommand.Add(newSubCommand);
//rootCommand.Add(continueSubCommand);

Option<string?> nameOption = new("--name")
{
    Description = "The name of the session to create."
};
nameOption.Aliases.Add("-n");
newSubCommand.Options.Add(nameOption);

Option<bool> useOldMenuOption = new("--old-menu")
{
    Description = "Use the old menu system instead of the new one."
};
newSubCommand.Options.Add(useOldMenuOption);

Option<int> pageSizeOption = new("--page-size")
{
    Description = "Number of menu items (admin options + entries) shown in the main menu before paging. Default: 30."
};
pageSizeOption.DefaultValueFactory = _ => 30;
newSubCommand.Options.Add(pageSizeOption);

newSubCommand.SetAction(parseResult => NewSession(
    parseResult.GetValue(nameOption),
    parseResult.GetValue(useOldMenuOption),
    parseResult.GetValue(pageSizeOption)
));

return rootCommand.Parse(args).Invoke();

static void NewSession(string? name, bool useOldMenu = false, int pageSize = 30)
{
    Session currentSession = Session.StartNew(name, useOldMenu, pageSize);

    // call the main session loop that does all the work
    currentSession.MainLoop();

    // the session ended gracefully (StopSession exit path) - stop the background
    // flush loop and force one final write so the on-disk file is up to date,
    // then let the process exit normally
    currentSession.Shutdown();
}
