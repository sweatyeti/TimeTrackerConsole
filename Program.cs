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

newSubCommand.SetAction(parseResult => NewSession(
    parseResult.GetValue(nameOption),
    parseResult.GetValue(useOldMenuOption)
));

return rootCommand.Parse(args).Invoke();

static void NewSession(string? name, bool useOldMenu = false)
{
    Session currentSession = Session.StartNew(name, useOldMenu);

    // call the main session loop that does all the work
    currentSession.MainLoop();

    // perform final flush: stop the background timer and write the last state to disk
    currentSession.FinalizeStore();
}
