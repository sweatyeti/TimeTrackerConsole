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
newSubCommand.SetAction(parseResult => NewSession(
    parseResult.GetValue(nameOption)
));

return rootCommand.Parse(args).Invoke();

static void NewSession(string? name)
{
    Session currentSession = Session.StartNew(name);

    // call the main session loop that does all the work
    currentSession.MainLoop();

}

