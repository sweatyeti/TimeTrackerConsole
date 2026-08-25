using Spectre.Console;

internal class Session
{
    private Session() { }

    private readonly Dictionary<int, TimeEntry> _timeEntries = new();
    private static bool _usingNewMenu = true;

    // owns the background flush loop, the dirty flag, and the mutation lock;
    // initialized in StartNew before any mutation can happen
    private EntryStore _store = null!;

    // set by the StopSession(exit: true) path so MainLoop unwinds gracefully
    // (instead of Environment.Exit) and Program.Main can run the final flush
    private bool _shouldExit;

    // main-menu page size (how many admin options + entries show before paging);
    // set from the --page-size CLI switch, defaults to 30
    private int _pageSize = 30;

    public string Name { get; set; } = string.Empty;
    public bool IsActive {get; private set;} = false;
    public int EntryCount => _timeEntries.Count;

    // persisted in the session file so a future import can identify the session
    // from the file contents rather than the filename
    public Guid SessionId { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }

    public static Session StartNew(string? name, bool useOldMenu, int pageSize = 30)
    {
        Session session = new();
        _usingNewMenu = !useOldMenu;

        if(String.IsNullOrEmpty(name))
        {
            name = $"Session {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        session.Name = name;
        session._pageSize = pageSize;
        session.SessionId = Guid.NewGuid();
        session.StartedAt = DateTime.Now;

        // wire up the on-disk store (creates entries/ and resolves the collision-free file path)
        session._store = new EntryStore(session, name);

        // create and populate the first time entry
        session.StartNewEntry();

        // start the background flush loop (immediate first check, then every 5 seconds)
        session._store.Start();

        return session;
    }

    // TODO: implement a load-from-file task that can be used to load a previous session and continue tracking time in it

    public void MainLoop()
    {
        while(!_shouldExit)
        {
            AnsiConsole.Clear();
            if(_usingNewMenu)
             {
                DisplaySummary();
                DisplayMainMenu();
             }
             else
             {
                DisplayEntries();
                DisplaySummary();
                PresentSessionMenu();
             }
        }
    }

    // graceful shutdown: stop the background flush loop and force one final write so
    // the on-disk file reflects the last <=5 seconds of changes before the process exits
    public void Shutdown()
    {
        _store.FlushAsync().GetAwaiter().GetResult();
    }

    // builds a detached copy of the live state under the mutation lock so the
    // background thread can serialize it without tearing (copy-then-serialize)
    internal SessionSnapshot TakeSnapshot()
    {
        lock(_store.MutationLock)
        {
            List<EntrySnapshot> entries = new();
            foreach(int id in _timeEntries.Keys.OrderBy(id => id))
            {
                TimeEntry entry = _timeEntries[id];
                entries.Add(new EntrySnapshot(
                    entry.Id,
                    entry.StartTime,
                    entry.IsComplete ? entry.EndTime : null,
                    entry.Task,
                    entry.Description,
                    entry.Logged,
                    entry.IsComplete));
            }

            return new SessionSnapshot(
                EntryStore.SchemaVersion,
                SessionId,
                Name,
                StartedAt,
                EndedAt,
                entries);
        }
    }

    private void DisplayEntries()
    {
        Table table = new Table()
            .MinimalDoubleHeadBorder()
            .BorderColor(Color.DarkOrange)
            .Title($"[cyan bold]{Markup.Escape(Name)}[/]");

        table.AddColumn("#");
        table.AddColumn("Start Time", col => col.Centered());
        table.AddColumn("End Time", col => col.Centered());
        table.AddColumn("Task", col => col.Centered());
        table.AddColumn("Logged", col => col.Centered());
        table.AddColumn("Description");

        for(int i = 1; i <= TimeEntry.LatestAssignedID; i++)
        {
            bool exists = _timeEntries.TryGetValue(i, out TimeEntry? entry);
            if(!exists || entry is null) continue;
            
            table.AddRow(entry.Id.ToString(), entry.StartTime.ToString("yyyy-MM-dd HH:mm"), entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm") : "[blue bold]In Progress[/]", Markup.Escape(entry.Task), entry.Logged ? "yes" : "no", Markup.Escape(entry.Description));
        }

        AnsiConsole.Write(table);
    }

    // ref: https://github.com/sweatyeti/MyTimeTracker/blob/main/BlazorTimeKeeper/Components/Pages/Home.razor
    private void DisplaySummary()
    {
        // if there are no entries then skip showing the summary section
        if(_timeEntries.Count == 0) return;

        var taskQuery = 
            from entry in _timeEntries.Values
            where entry.IsComplete == true
            group entry by entry.Task.ToLower() into taskGroup
            select new
            {
                Task = taskGroup.Key,
                EntryCount = taskGroup.Count(),
                TotalMins = taskGroup.Sum(s => Math.Ceiling((s.EndTime - s.StartTime).TotalMinutes)),
                UnloggedMins = taskGroup.Sum(s => Math.Ceiling(s.Logged ? 0 : (s.EndTime - s.StartTime).TotalMinutes))
            };
            
        Table table = new Table()
            .MarkdownBorder()
            .BorderColor(Color.Blue)
            .Title("[cyan bold]Summary[/]");

        table.AddColumns("Task", "Count", "Unlogged (hh:mm)", "Total (hh:mm)");

        // totals accumulated only across non-empty-task groups (issue #15)
        int totalEntryCount = 0;
        double totalUnloggedMins = 0;
        double totalTotalMins = 0;

        foreach(var taskGroup in taskQuery)
        {
            bool emptyTask = string.IsNullOrEmpty(taskGroup.Task) || taskGroup.Task.Equals("none", StringComparison.OrdinalIgnoreCase);
            if(!emptyTask)
            {
                totalEntryCount += taskGroup.EntryCount;
                totalUnloggedMins += taskGroup.UnloggedMins;
                totalTotalMins += taskGroup.TotalMins;
            }

            table.AddRow(Markup.Escape(taskGroup.Task), taskGroup.EntryCount.ToString(), $"{TimeSpan.FromMinutes(taskGroup.UnloggedMins):hh\\:mm}", $"{TimeSpan.FromMinutes(taskGroup.TotalMins):hh\\:mm}");
        }

        // totals row - excludes entries that aren't part of a task (the "none"/empty group)
        table.AddRow("[bold]Total[/]", totalEntryCount.ToString(), $"{TimeSpan.FromMinutes(totalUnloggedMins):hh\\:mm}", $"{TimeSpan.FromMinutes(totalTotalMins):hh\\:mm}");

        AnsiConsole.Write(table);
    }

    private void DisplayMainMenu()
    {
        // this presents a menu where the top portion contains admin-type stuff like stopping/starting, logging a task group, exiting, etc.
        // underneath that is the selectable list of entries

        /* Layout looks like:
         *  Stop current entry and start a new one
         *  Log a task group
         *  Stop tracking
         *  Exit
         *  [list of selectable entries with details]
        */

        // need to determine how many choices will be presented to the user
        int choiceCount = 4; // for the static options (stop/start, log task group, stop tracking, exit)
        choiceCount += _timeEntries.Count; // add the number of entries to the choice count

        int[] entryChoices = new int[choiceCount];
        // using values <0 for admin options, and values >0 for entry options (corresponding to entry IDs)
        // note: zero/0 as a value with not be used (so far) as entry IDs start at 1 and increment, and admin options are <0
        entryChoices[0] = -1; // stop/start option
        entryChoices[1] = -2; // log task group option
        entryChoices[2] = -3; // stop tracking option
        entryChoices[3] = -4; // exit option

        int i = 4;
        foreach(int entryId in _timeEntries.Keys.Reverse())
        {
            entryChoices[i] = entryId; 
            i++;
        }

        // the prompt under-the-hood works with the int values in entryChoices, but the converter will display the appropriate string for each choice (either a static admin option or an entry's details depending on the value)
        SelectionPrompt<int> theMenu = new SelectionPrompt<int>()
            .AddChoices(entryChoices)
            .PageSize(_pageSize)
            .WrapAround()
            .UseConverter(MainMenuConverter);

        AnsiConsole.MarkupLine("[orange1 bold]Select an [Chartreuse2]option[/] or [CadetBlue]entry[/] to update:[/]");

        int userChoice = theMenu.Show(AnsiConsole.Console);

        // take the selected value and pass that into a switch to determine what to do
        switch(userChoice)
        {
            case -1:
                StopCurrentEntry();
                StartNewEntry();
                break;
            case -2:
                LogTaskGroupFlow();
                break;
            case -3:
                StopSession(exit: false);
                break;
            case -4:
                StopSession(exit: true);
                break;
            default:
                if(_timeEntries.ContainsKey(userChoice))
                {
                    UpdateEntryFlow(userChoice);
                }
                else
                {
                    // should not reach here, but putting catch-all just in case
                    AnsiConsole.MarkupLine("[red bold]Invalid choice. Press any key to continue...[/]");
                    AnsiConsole.Console.Input.ReadKey(true);
                }
                break;
        }
        

    }

    private string MainMenuConverter(int choice)
    {
        // this gets passed the int value for each choice in the menu during rendering, so generate the appropriate display string for each

        string result = choice switch
        {
            -1 => IsActive ? "[Chartreuse2]Stop current entry and start a new one[/]" : "[Chartreuse2]Start a new entry[/]",
            -2 => "[Chartreuse2]Log a task group[/]",
            -3 => "[Chartreuse2]Stop tracking[/]",
            -4 => "[Chartreuse2]Stop and exit[/]",
            _ => string.Empty
        };
        if(result != string.Empty) return result;

        // if the choice is not one of the static options, then it must be an entry choice, so find the entry with the matching ID and return its details as the converter result
        if(_timeEntries.TryGetValue(choice, out TimeEntry? entry))
        {
            result = $"[CadetBlue]#{entry.Id} | Task: {Markup.Escape(entry.Task)} | {entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "[blue bold]In Progress[/]")} {(entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : entry.IsComplete ? entry.Logged ? "| [green]Logged[/]" : "| [red]Unlogged[/]" : string.Empty)} | {(String.IsNullOrEmpty(entry.Description) ? "[gray]No description[/]" : $"{Markup.Escape(entry.Description)}")}[/]";
        }

        return result;
    }

    private void PresentSessionMenu()
    {
        SelectionPrompt<int> initialPrompt = new SelectionPrompt<int>()
            .Title("Pick one:")
            .AddChoices(new[] { 1, 2, 3, 4, 5, 6 })
            .UseConverter(choice => choice switch
            {
                1 => IsActive ? "Stop current entry and start a new one" : "Start a new entry",
                2 => "Update an entry",
                3 => "Log a task group",
                4 => "Delete an entry",
                5 => "Stop current session",
                6 => "Stop and exit",
                _ => throw new InvalidOperationException()
            });

            int userChoice = initialPrompt.Show(AnsiConsole.Console);

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
        TimeEntry newEntry = TimeEntry.GetNextEntry();
        TextPrompt<string> entryTaskPrompt = new TextPrompt<string>("Entry started, enter a task if desired:")
            .AllowEmpty()
            .ShowDefaultValue(false);
        string entryTask = AnsiConsole.Prompt(entryTaskPrompt);
        string trimmedEntryTask = entryTask.Trim();
        if(String.IsNullOrEmpty(trimmedEntryTask)) trimmedEntryTask = "none";

        newEntry.Task = trimmedEntryTask;
        lock(_store.MutationLock)
        {
            _timeEntries[newEntry.Id] = newEntry;
        }
        IsActive = true;
        _store.MarkDirty();
    }

    private void StopCurrentEntry()
    {
        if(_timeEntries.Count == 0 || !IsActive) return;

        TimeEntry currentEntry = _timeEntries[TimeEntry.LatestAssignedID];
        if(currentEntry.IsComplete) return;

        lock(_store.MutationLock)
        {
            currentEntry.EndTime = DateTime.Now;
            currentEntry.IsComplete = true;
        }
        IsActive = false;
        _store.MarkDirty();
    }

    private void UpdateEntryFlow(int entryId = 0)
    {
        TimeEntry? selectedEntry;
        if(entryId == 0 && !_usingNewMenu)
        {
            if(_timeEntries.Count == 0)
            {
                AnsiConsole.MarkupLine("[red bold]No entries to update.[/]");
                return;
            }

            SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
                .Title("Select an entry to update:")
                .AddChoices(_timeEntries.Values)
                .UseConverter(entry => $"Id: {entry.Id} {Markup.Escape(entry.Task)} ({entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "[blue bold]In Progress[/]")} {(entry.IsComplete ? entry.Logged ? "[green](Logged)[/]" : "[red](Unlogged)[/]" : string.Empty)})");

            entryPrompt.CancelResult = () => TimeEntry.GetEmpty(); // this will return an empty (invalid) entry to check against
            selectedEntry = entryPrompt.Show(AnsiConsole.Console);
        }
        else
        {
            if(!_timeEntries.TryGetValue(entryId, out selectedEntry))
            {
                AnsiConsole.MarkupLine("[red bold]Selected entry not found.[/]");
                return;
            }
        }

        // check if prompt was cancelled by checking if the returned TimeEntry is invalid
        if(!selectedEntry.IsValid) return;

        // gather ALL prompt inputs first, then apply them in a single atomic
        // mutation under the lock (was three separate lock blocks - the entry
        // update can no longer be persisted half-applied by a mid-flow flush)
        bool? updatedLogged = null;
        if(selectedEntry.IsComplete && !selectedEntry.Task.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
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

            updatedLogged = isItLoggedPrompt.Show(AnsiConsole.Console);
        }

        TextPrompt<string> updatedEntryTaskPrompt = new TextPrompt<string>($"Update entry's task (current: {Markup.Escape(selectedEntry.Task)}):")
            .AllowEmpty()
            .DefaultValue(selectedEntry.Task)
            .ShowDefaultValue(false);
        string updatedEntryTask = updatedEntryTaskPrompt.Show(AnsiConsole.Console);

        TextPrompt<string> updatedEntryDescriptionPrompt = new TextPrompt<string>($"Update entry's description (current: {Markup.Escape(selectedEntry.Description)}):")
            .AllowEmpty()
            .DefaultValue(selectedEntry.Description)
            .ShowDefaultValue(false);

        string updatedEntryDescription = updatedEntryDescriptionPrompt.Show(AnsiConsole.Console);

        lock(_store.MutationLock)
        {
            if(updatedLogged.HasValue) selectedEntry.Logged = updatedLogged.Value;
            selectedEntry.Task = updatedEntryTask.Trim();
            selectedEntry.Description = updatedEntryDescription.Trim();
        }

        _store.MarkDirty();
    }

    private void LogTaskGroupFlow()
    {
        // get distinct task groups from entries that still have unlogged, completed work
        IEnumerable<string> taskGroups = _timeEntries.Values
            .Where(entry => !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase)
                         && entry.IsComplete
                         && !entry.Logged)
            .Select(entry => entry.Task)
            .Distinct();

        if(!taskGroups.Any())
        {
            AnsiConsole.MarkupLine("[red bold]No task groups with unlogged entries to log. Press any key to continue...[/]");
            AnsiConsole.Console.Input.ReadKey(true);
            return;
        }

        SelectionPrompt<string> taskGroupPrompt = new SelectionPrompt<string>()
            .Title("Select a task group to log (press ESC to cancel):")
            .AddChoices(taskGroups)
            .UseConverter(taskGroup => $"{Markup.Escape(taskGroup)} ({_timeEntries.Values.Count(entry => entry.Task.Equals(taskGroup, StringComparison.OrdinalIgnoreCase) && entry.IsComplete && !entry.Logged)} unlogged)");

        taskGroupPrompt.CancelResult = () => string.Empty;

        string selectedTaskGroup = taskGroupPrompt.Show(AnsiConsole.Console);

        if(string.IsNullOrEmpty(selectedTaskGroup)) return;
        
        IEnumerable<TimeEntry> entriesInTaskGroup = _timeEntries.Values.Where(entry => entry.Task.Equals(selectedTaskGroup, StringComparison.OrdinalIgnoreCase));

        // single acquisition for the whole group: the lazy LINQ enumeration rides
        // inside the lock, so the snapshot can never see a partially-logged group
        lock(_store.MutationLock)
        {
            foreach(TimeEntry entry in entriesInTaskGroup)
            {
                if(!entry.IsComplete) continue; // skip in progress entries, only log completed entries
                entry.Logged = true;
            }
        }
        _store.MarkDirty();
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
            .AddChoices(_timeEntries.Values)
            .UseConverter(entry => $"Id: {entry.Id} {Markup.Escape(entry.Task)} ({entry.StartTime:yyyy-MM-dd HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm") : "In Progress")})");
        
        entryPrompt.CancelResult = () => TimeEntry.GetEmpty();
        
        TimeEntry selectedEntry = entryPrompt.Show(AnsiConsole.Console);

        if (!selectedEntry.IsValid || !AnsiConsole.Confirm("Are you sure you want to delete this entry?", defaultValue: true))
        {
            return;
        }

        // if the entry being deleted is currently active, flip the session's active switch to false before deleting the entry
        if(selectedEntry == _timeEntries[TimeEntry.LatestAssignedID] && IsActive && !selectedEntry.IsComplete)
        {
            IsActive = false;
        }
        lock(_store.MutationLock)
        {
            _timeEntries.Remove(selectedEntry.Id);
        }
        _store.MarkDirty();
    }

    // IN PROGRESS
    private void StopSession(bool exit = false)
    {
        StopCurrentEntry();

        if (exit)
        {
            // mark the session as ended, flag the state as dirty, then let MainLoop
            // unwind so Program.Main can perform the final on-disk flush before the
            // process exits normally
            lock(_store.MutationLock)
            {
                EndedAt = DateTime.Now;
            }
            _store.MarkDirty();

            AnsiConsole.Clear();
            DisplayEntries();
            DisplaySummary();
            _shouldExit = true;
            // Environment.Exit(0); // replaced by the graceful unwind above so the final flush can run
        }
    }
}
