using System.Linq;
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
    public int EntryCount => _timeEntries.Values.Count(entry => !entry.IsDeleted);

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

    // resumes a session from a previously saved snapshot, continuing to write
    // to the same file on disk
    public static Session Resume(SessionSnapshot snapshot, string filePath, bool useOldMenu, int pageSize)
    {
        Session session = new();
        _usingNewMenu = !useOldMenu;

        session.Name = snapshot.Name ?? "Unnamed session";
        session._pageSize = pageSize;
        session.SessionId = snapshot.SessionId;
        session.StartedAt = snapshot.StartedAt;
        session.EndedAt = null; // resuming means the session is active again

        // wire up the store targeting the EXISTING file (no collision-free path)
        session._store = EntryStore.ForExistingFile(session, filePath);

        // reconstruct entries from the snapshot
        int maxId = 0;
        lock(session._store.MutationLock)
        {
            foreach(EntrySnapshot es in snapshot.Entries)
            {
                TimeEntry entry = TimeEntry.FromSnapshot(
                    es.Id, es.StartTime, es.EndTime, es.Task,
                    es.Description, es.Logged, es.IsComplete, es.IsDeleted);
                session._timeEntries[entry.Id] = entry;
                if(es.Id > maxId) maxId = es.Id;
            }
        }

        // reseed the static ID counter so new entries don't collide with existing IDs
        TimeEntry.ReseedId(maxId + 1);

        // if there is an in-progress (non-deleted, not complete) entry the session is active
        session.IsActive = session._timeEntries.Values.Any(e => !e.IsDeleted && !e.IsComplete);

        // start the background flush loop, then mark dirty so the reactivated state
        // (EndedAt back to null) is persisted to the file on the next flush
        session._store.Start();
        session._store.MarkDirty();

        return session;
    }

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
            // deleted entries are deliberately kept in the snapshot so they survive a
            // restart and can later be viewed/restored from the "View deleted entries" menu
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
                    entry.IsComplete,
                    entry.IsDeleted));
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
            if(!exists || entry is null || entry.IsDeleted) continue;

            table.AddRow(entry.Id.ToString(), entry.StartTime.ToString("yyyy-MM-dd HH:mm"), entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm") : "[blue bold]In Progress[/]", Markup.Escape(entry.Task), entry.Logged ? "yes" : "no", Markup.Escape(entry.Description));
        }

        AnsiConsole.Write(table);
    }

    // ref: https://github.com/sweatyeti/MyTimeTracker/blob/main/BlazorTimeKeeper/Components/Pages/Home.razor
    private void DisplaySummary()
    {
        // if there are no (non-deleted) entries then skip showing the summary section
        if(EntryCount == 0) return;

        var taskQuery = 
            from entry in _timeEntries.Values
            where entry.IsComplete == true && !entry.IsDeleted
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
        double totalUnloggedMins = 0;
        double totalTotalMins = 0;

        foreach(var taskGroup in taskQuery)
        {
            bool emptyTask = string.IsNullOrEmpty(taskGroup.Task) || taskGroup.Task.Equals("none", StringComparison.OrdinalIgnoreCase);
            if(!emptyTask)
            {
                totalUnloggedMins += taskGroup.UnloggedMins;
                totalTotalMins += taskGroup.TotalMins;
            }

            table.AddRow(Markup.Escape(taskGroup.Task), taskGroup.EntryCount.ToString(), $"{TimeSpan.FromMinutes(taskGroup.UnloggedMins):hh\\:mm}", $"{TimeSpan.FromMinutes(taskGroup.TotalMins):hh\\:mm}");
        }

        AnsiConsole.Write(table);

        // issue #15: totals render as a single line BETWEEN the summary table and the
        // menu/list (not as a row inside the table); excludes entries not part of a task
        RenderTotalsLine(totalUnloggedMins, totalTotalMins);
    }

    // prints e.g. "Total unlogged task time: 01:15   Total time: 02:40" as its own line
    // (trailing blank line separates it from the menu that follows)
    private void RenderTotalsLine(double totalUnloggedMins, double totalTotalMins)
    {
        AnsiConsole.MarkupLine($"[bold]Total unlogged task time:[/] {TimeSpan.FromMinutes(totalUnloggedMins):hh\\:mm}    [bold]Total time:[/] {TimeSpan.FromMinutes(totalTotalMins):hh\\:mm}");
        AnsiConsole.WriteLine();
    }

    private void DisplayMainMenu()
    {
        // this presents a menu where the top portion contains admin-type stuff like stopping/starting, logging a task group, exiting, etc.
        // underneath that is the selectable list of entries

        /* Layout looks like:
         *  Stop current entry and start a new one
         *  Log a task group
         *  View deleted entries
         *  Stop tracking
         *  Exit
         *  [list of selectable entries with details]
        */

        // build the choices densely: static admin options (<0) first, then the
        // selectable entry IDs (>0) in reverse order. deleted entries are kept in
        // _timeEntries but are NOT presented here - they're only reachable via the
        // "View deleted entries" menu, so skip them when filling the choice list
        // (sizing from the non-deleted count avoids 0-valued holes in the array)
        List<int> entryChoices = new()
        {
            -1, // stop/start option
            -2, // log task group option
            -5, // view deleted entries option
            -3, // stop tracking option
            -4  // exit option
        };
        foreach(int entryId in _timeEntries.Keys.Reverse())
        {
            if(_timeEntries[entryId].IsDeleted) continue;
            entryChoices.Add(entryId);
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
            case -5:
                ViewDeletedEntriesFlow();
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
            -5 => "[Chartreuse2]View deleted entries[/]",
            _ => string.Empty
        };
        if(result != string.Empty) return result;

        // if the choice is not one of the static options, then it must be an entry choice, so find the entry with the matching ID and return its details as the converter result
        // (deleted entries are never presented as choices, but guard against rendering one just in case)
        if(_timeEntries.TryGetValue(choice, out TimeEntry? entry) && !entry.IsDeleted)
        {
            result = BuildEntryRow(entry);
        }

        return result;
    }

    // builds one entry row for the main menu. Spectre pads each menu row to the
    // same total width, but it does NOT align fields WITHIN a row, so every
    // variable-width field is padded to a fixed column width (derived from the
    // live entry set) to keep the columns visually aligned (issue #19).
    private string BuildEntryRow(TimeEntry entry)
    {
        // column widths: id, task, time, status, description are all variable
        int idWidth = _timeEntries.Values
            .Where(e => !e.IsDeleted)
            .Select(e => e.Id.ToString().Length)
            .DefaultIfEmpty(0)
            .Max();

        int taskWidth = _timeEntries.Values
            .Where(e => !e.IsDeleted)
            .Select(e => e.Task.Length)
            .DefaultIfEmpty(0)
            .Max();

        // time column: "HH:mm - HH:mm" for completed entries, "HH:mm - In
        // Progress" for the active entry — variable, so pad it to the widest
        // time string to keep the trailing columns aligned (issue #19).
        int timeWidth = _timeEntries.Values
            .Where(e => !e.IsDeleted)
            .Select(e => e.IsComplete
                ? $"{e.StartTime:HH:mm} - {e.EndTime:HH:mm}".Length
                : $"{e.StartTime:HH:mm} - In Progress".Length)
            .DefaultIfEmpty(0)
            .Max();

        // status text: In Progress / Logged / Unlogged (the logged/unlogged
        // marker is only shown for completed entries with a real task)
        string loggedText = entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : entry.IsComplete
                ? entry.Logged ? "Logged" : "Unlogged"
                : string.Empty;

        string idPart = $"#{entry.Id}".PadRight(idWidth + 1);
        string taskPart = Markup.Escape(entry.Task).PadRight(taskWidth);

        // pad the PLAIN time text to the fixed width, then wrap in markup so the
        // tags don't count against the pad (markup is zero-width when rendered)
        string timeText = $"{entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "In Progress")}".PadRight(timeWidth);
        string timePart = entry.IsComplete ? timeText : $"[blue bold]{timeText}[/]";

        string row = $"[CadetBlue]{idPart} | {taskPart} | {timePart}";
        if(loggedText.Length > 0)
        {
            row += $" | {(entry.Logged ? "[green]Logged[/]" : "[red]Unlogged[/]")}";
        }
        row += $" | {(string.IsNullOrEmpty(entry.Description) ? "[gray]No description[/]" : Markup.Escape(entry.Description))}[/]";

        return row;
    }

    private void PresentSessionMenu()
    {
        SelectionPrompt<int> initialPrompt = new SelectionPrompt<int>()
            .Title("Pick one:")
            .AddChoices(new[] { 1, 2, 3, 4, 5, 6, 7 })
            .UseConverter(choice => choice switch
            {
                1 => IsActive ? "Stop current entry and start a new one" : "Start a new entry",
                2 => "Update an entry",
                3 => "Log a task group",
                4 => "Delete an entry",
                5 => "View deleted entries",
                6 => "Stop current session",
                7 => "Stop and exit",
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
                    ViewDeletedEntriesFlow();
                    break;
                case 6:
                    StopSession(exit: false);
                    break;
                case 7:
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
            if(EntryCount == 0)
            {
                AnsiConsole.MarkupLine("[red bold]No entries to update.[/]");
                return;
            }

            SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
                .Title("Select an entry to update:")
                .AddChoices(_timeEntries.Values.Where(entry => !entry.IsDeleted))
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

        // issue #12: deletion lives inside the edit entry view. only completed,
        // non-deleted entries can be deleted - soft delete sets the IsDeleted flag
        // (the entry stays in the store and is only reachable via "View deleted entries")
        if(selectedEntry.IsComplete && !selectedEntry.IsDeleted
           && AnsiConsole.Confirm($"Delete this entry? (id {selectedEntry.Id}, task '{Markup.Escape(selectedEntry.Task)}')", defaultValue: false))
        {
            lock(_store.MutationLock)
            {
                selectedEntry.IsDeleted = true;
            }
            _store.MarkDirty();
            return;
        }

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
        // get distinct task groups from entries that still have unlogged, completed work (deleted entries don't count)
        IEnumerable<string> taskGroups = _timeEntries.Values
            .Where(entry => !entry.IsDeleted
                         && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase)
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
            .UseConverter(taskGroup => $"{Markup.Escape(taskGroup)} ({_timeEntries.Values.Count(entry => !entry.IsDeleted && entry.Task.Equals(taskGroup, StringComparison.OrdinalIgnoreCase) && entry.IsComplete && !entry.Logged)} unlogged)");

        taskGroupPrompt.CancelResult = () => string.Empty;

        string selectedTaskGroup = taskGroupPrompt.Show(AnsiConsole.Console);

        if(string.IsNullOrEmpty(selectedTaskGroup)) return;
        
        IEnumerable<TimeEntry> entriesInTaskGroup = _timeEntries.Values.Where(entry => !entry.IsDeleted && entry.Task.Equals(selectedTaskGroup, StringComparison.OrdinalIgnoreCase));

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
        // only completed entries can be deleted, and already-deleted ones are not shown again
        IEnumerable<TimeEntry> deletableEntries = _timeEntries.Values.Where(entry => entry.IsComplete && !entry.IsDeleted);
        if(!deletableEntries.Any())
        {
            AnsiConsole.MarkupLine("[red bold]No completed entries to delete.[/]");
            return;
        }

        SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
            .Title("Select an entry to delete:")
            .AddChoices(deletableEntries)
            .UseConverter(entry => $"Id: {entry.Id} {Markup.Escape(entry.Task)} ({entry.StartTime:yyyy-MM-dd HH:mm} - {entry.EndTime.ToString("yyyy-MM-dd HH:mm")})");

        entryPrompt.CancelResult = () => TimeEntry.GetEmpty();

        TimeEntry selectedEntry = entryPrompt.Show(AnsiConsole.Console);

        if (!selectedEntry.IsValid || !AnsiConsole.Confirm("Are you sure you want to delete this entry?", defaultValue: true))
        {
            return;
        }

        // soft delete: flag the entry instead of removing it from the store so it can
        // later be viewed and restored from the "View deleted entries" menu
        lock(_store.MutationLock)
        {
            selectedEntry.IsDeleted = true;
        }
        _store.MarkDirty();
    }

    // the only place deleted entries are viewable: lists them oldest-first and
    // restores the selected one by flipping its IsDeleted flag back to false
    private void ViewDeletedEntriesFlow()
    {
        IEnumerable<TimeEntry> deletedEntries = _timeEntries.Values.Where(entry => entry.IsDeleted).OrderBy(entry => entry.Id);
        if(!deletedEntries.Any())
        {
            AnsiConsole.MarkupLine("[red bold]No deleted entries. Press any key to continue...[/]");
            AnsiConsole.Console.Input.ReadKey(true);
            return;
        }

        SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
            .Title("Select a deleted entry to restore (press ESC to cancel):")
            .AddChoices(deletedEntries)
            .UseConverter(entry => $"Id: {entry.Id} {Markup.Escape(entry.Task)} ({entry.StartTime:yyyy-MM-dd HH:mm} - {entry.EndTime.ToString("yyyy-MM-dd HH:mm")}) [red](deleted)[/]");

        entryPrompt.CancelResult = () => TimeEntry.GetEmpty(); // this will return an empty (invalid) entry to check against

        TimeEntry selectedEntry = entryPrompt.Show(AnsiConsole.Console);

        // check if prompt was cancelled by checking if the returned TimeEntry is invalid
        if(!selectedEntry.IsValid) return;

        if(!AnsiConsole.Confirm("Restore this entry?", defaultValue: true))
        {
            return;
        }

        lock(_store.MutationLock)
        {
            selectedEntry.IsDeleted = false;
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
