using Spectre.Console;

internal class Session
{
    private Session() { }

    private List<TimeEntry> _timeEntries = new(10);

    public string Name { get; set; } = string.Empty;
    public bool IsActive {get; private set;} = false;
    public int EntryCount => _timeEntries.Count;

    public static Session StartNew(string? name)
    {
        Session session = new();

        if(String.IsNullOrEmpty(name))
        {
            name = $"Session {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        session.Name = name;

        // create and populate the first time entry
        session.StartNewEntry();

        // flip the active switch to true
        session.IsActive = true;

        return session;
    }

// TODO: implement a save-to-file task that runs whenever a change is made to the session's entries
// TODO: implement a load-from-file task that can be used to load a previous session and continue tracking time in it

    public void MainLoop()
    {
        // the loop flow is:
        // start 
        // -> clear display
        // -> show entries and summary 
        // -> read user input to determine next action (stop and start new, update current task or description, or stop and/or exit) 
        // -> repeat       

        while(true)
        {
            AnsiConsole.Clear();
            DisplayEntries();
            DisplaySummary();
            PresentSessionMenu(); // right now this exits if needed, should perhaps return a bool to indicate whether to exit or not instead of having the exit logic in this method
        }
    }

    private void DisplayEntries()
    {
        Table table = new Table()
            .MinimalDoubleHeadBorder()
            .BorderColor(Color.DarkOrange)
            .Title($"[cyan bold]{Name}[/]");

        //table.AddColumns("Id", "Start Time", "End Time", "Task", "Description");
        table.AddColumn("Id");
        table.AddColumn("Start Time", col => col.Centered());
        table.AddColumn("End Time", col => col.Centered());
        table.AddColumn("Task", col => col.Centered());
        table.AddColumn("Logged", col => col.Centered());
        table.AddColumn("Description");

        for(byte i = 0; i < _timeEntries.Count; i++)
        {
            TimeEntry? entry = _timeEntries[i];
            if(entry is null)
            {
                continue;
            }
            table.AddRow(entry.Id.ToString(), entry.StartTime.ToString("yyyy-MM-dd HH:mm:ss"), entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm:ss") : "In Progress", entry.Task, entry.Logged ? "yes" : "no", entry.Description);
        }

        AnsiConsole.Write(table);
    }

    // ref: https://github.com/sweatyeti/MyTimeTracker/blob/main/BlazorTimeKeeper/Components/Pages/Home.razor
    private void DisplaySummary()
    {
        if(_timeEntries.Count == 0 || (_timeEntries.Count == 1 && !_timeEntries[0].IsComplete))
        {
            return;
        }

        Table table = new Table()
            .MarkdownBorder()
            .BorderColor(Color.Blue)
            .Title("[cyan bold]Summary[/]");

        table.AddColumns("Task", "Entry Count", "Unlogged Time (hh:mm)", "Total Time (hh:mm)");

        var taskQuery = 
            from entry in _timeEntries
            where entry.IsComplete == true
            group entry by entry.Task.ToLower() into taskGroup
            select new
            {
                Task = taskGroup.Key,
                EntryCount = taskGroup.Count(),
                TotalMins = taskGroup.Sum(s => Math.Ceiling((s.EndTime - s.StartTime).TotalMinutes)),
                UnloggedMins = taskGroup.Sum(s => Math.Ceiling(s.Logged ? 0 : (s.EndTime - s.StartTime).TotalMinutes))
            };

            TimeSpan totalTaskTimeForDay = TimeSpan.Zero;

            foreach(var taskGroup in taskQuery)
            {
                bool emptyTask = string.IsNullOrEmpty(taskGroup.Task);
                if(!emptyTask)
                {
                    totalTaskTimeForDay += TimeSpan.FromMinutes(taskGroup.TotalMins);
                }

                table.AddRow(taskGroup.Task, taskGroup.EntryCount.ToString(), $"{TimeSpan.FromMinutes(taskGroup.UnloggedMins):hh\\:mm}", $"{TimeSpan.FromMinutes(taskGroup.TotalMins):hh\\:mm}");
            }

            AnsiConsole.Write(table);
    }

    private void PresentSessionMenu()
    {
        SelectionPrompt<int> initialPrompt = new SelectionPrompt<int>()
            .Title("Pick one:")
            .AddChoices(new[] { 1, 2, 3, 4, 5, 6})
            .UseConverter(choice => choice switch
            {
                1 => IsActive ? "Stop current entry and start a new one" : "Start a new entry",
                2 => "Update an entry",
                3 => "Log a task group",
                4 => "Delete an entry",
                5 => "Stop current session",
                6 => "Exit",
                _ => throw new InvalidOperationException()
            });

            int userChoice = AnsiConsole.Prompt(initialPrompt);

            switch (userChoice)
            {
                case 1:
                    StopCurrentEntry();
                    StartNewEntry();
                    break;
                case 2:
                    UpdateEntryFlow();
                    break;
                case 3:
                    LogTaskGroupFlow();
                    break;
                case 4:
                    DeleteEntryFlow();
                    break;
                case 5:
                    StopSession(exit: false);
                    break;
                case 6:
                    StopSession(exit: true);
                    break;
                default:
                    throw new InvalidOperationException();

            }
    }

    private void StartNewEntry()
    {
        _timeEntries.Add(TimeEntry.GetNextEntry());
        IsActive = true;
    }

    private void StopCurrentEntry()
    {
        if(_timeEntries.Count == 0 || !IsActive) return;

        TimeEntry currentEntry = _timeEntries[^1];
        if(currentEntry.EndTime == DateTime.MinValue)
        {
            currentEntry.EndTime = DateTime.Now;
            currentEntry.IsComplete = true;
        }

        IsActive = false;
    }

    private void UpdateEntryFlow()
    {
        if(_timeEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red bold]No entries to update.[/]");
            return;
        }

        SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
            .Title("Select an entry to update:")
            .AddChoices(_timeEntries)
            .UseConverter(entry => $"Id: {entry.Id} {entry.Task} ({entry.StartTime:yyyy-MM-dd HH:mm:ss} - {(entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm:ss") : "In Progress")} {(entry.IsComplete ? entry.Logged ? "[green](Logged)[/]" : "[red](Unlogged)[/]" : string.Empty)})");

        TimeEntry selectedEntry = AnsiConsole.Prompt(entryPrompt);

        TextPrompt<bool> isItLoggedPrompt = new TextPrompt<bool>($"Is this entry logged? (current: {(selectedEntry.Logged ? "yes" : "no")})")
            .AddChoice(true)
            .AddChoice(false)
            .DefaultValue(selectedEntry.Logged)
            .ShowDefaultValue(false)
            .WithConverter(choice => choice switch
            {
                true => "y",
                false => "n"
            });

        bool isItLogged = AnsiConsole.Prompt(isItLoggedPrompt);
        selectedEntry.Logged = isItLogged;

        TextPrompt<string> updatedEntryTaskPrompt = new TextPrompt<string>($"Update entry's task (current: {selectedEntry.Task}):")
            .AllowEmpty()
            .DefaultValue(selectedEntry.Task)
            .ShowDefaultValue(false);
        string updatedEntryTask = AnsiConsole.Prompt(updatedEntryTaskPrompt);
        selectedEntry.Task = updatedEntryTask.Trim();

        TextPrompt<string> updatedEntryDescriptionPrompt = new TextPrompt<string>($"Update entry's description (current: {selectedEntry.Description}):")
            .AllowEmpty()
            .DefaultValue(selectedEntry.Description)
            .ShowDefaultValue(false);

        string updatedEntryDescription = AnsiConsole.Prompt(updatedEntryDescriptionPrompt);
        selectedEntry.Description = updatedEntryDescription.Trim();
    }

    private void LogTaskGroupFlow()
    {
        // get distinct task groups from entries
        IEnumerable<string> taskGroups = _timeEntries
            .Where(entry => !string.IsNullOrEmpty(entry.Task))
            .Select(entry => entry.Task)
            .Distinct();

        if(!taskGroups.Any())
        {
            AnsiConsole.MarkupLine("[red bold]No task groups to log. Press any key to continue...[/]");
            AnsiConsole.Console.Input.ReadKey(true);
            return;
        }

        SelectionPrompt<string> taskGroupPrompt = new SelectionPrompt<string>()
            .Title("Select a task group to log:")
            .AddChoices(taskGroups)
            .UseConverter(taskGroup => $"{taskGroup} ({_timeEntries.Count(entry => entry.Task.Equals(taskGroup, StringComparison.OrdinalIgnoreCase))} entries)");

        string selectedTaskGroup = AnsiConsole.Prompt(taskGroupPrompt);

        IEnumerable<TimeEntry> entriesInTaskGroup = _timeEntries.Where(entry => entry.Task.Equals(selectedTaskGroup, StringComparison.OrdinalIgnoreCase));

        foreach(var entry in entriesInTaskGroup)
        {
            entry.Logged = true;
        }
    }

    private void DeleteEntryFlow()
    {
        if(_timeEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red bold]No entries to delete.[/]");
            return;
        }

        SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
            .Title("Select an entry to delete:")
            .AddChoices(_timeEntries)
            .UseConverter(entry => $"Id: {entry.Id} {entry.Task} ({entry.StartTime:yyyy-MM-dd HH:mm:ss} - {(entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm:ss") : "In Progress")})");
        TimeEntry selectedEntry = AnsiConsole.Prompt(entryPrompt);

        if (!AnsiConsole.Confirm("Are you sure you want to delete this entry?", defaultValue: true))
        {
            return;
        }

        // if the entry being deleted is currently active, flip the session's active switch to false before deleting the entry
        if(selectedEntry == _timeEntries[^1] && IsActive && selectedEntry.EndTime == DateTime.MinValue)
        {
            IsActive = false;
        }
        _timeEntries.Remove(selectedEntry);
    }

    // IN PROGRESS
    private void StopSession(bool exit = false)
    {
        StopCurrentEntry();

        if (exit)
        {
            Environment.Exit(0);
        }
    }
}