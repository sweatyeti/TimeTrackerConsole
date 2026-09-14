using System.Linq;
using Spectre.Console;

internal class Session
{
    private Session() { }

    // width of the Logged/Unlogged/N-A status column in the main-menu entry rows:
    // the wider of the labels, so everything pads out to the same width.
    // internal so the Terminal.Gui entry-row renderer pads to the same width
    internal const int StatusColumnWidth = 8;

    private ConsoleTheme _theme = null!;

    private readonly Dictionary<int, TimeEntry> _timeEntries = new();

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

    public static Session StartNew(string? name, int pageSize = 30, ConsoleTheme? theme = null)
    {
        // the Spectre path prompts for the first entry's task as part of creating the session
        Session session = StartNewCore(name, pageSize, theme);
        session.StartNewEntry();
        session._store.Start();

        return session;
    }

    // Phase 2 (Terminal.Gui migration): the TUI creates a real, WRITABLE session - same
    // EntryStore, same background flush loop, so the session JSON is written exactly as the
    // Spectre path writes it. It differs only in the first entry's task: the Spectre path
    // prompts inline, the TUI collects it in a dialog and calls StartNewEntryWithTask.
    internal static Session StartNewForTui(string? name, int pageSize = 30, ConsoleTheme? theme = null)
    {
        Session session = StartNewCore(name, pageSize, theme);
        session._store.Start();

        return session;
    }

    private static Session StartNewCore(string? name, int pageSize, ConsoleTheme? theme)
    {
        Session session = new();

        if(String.IsNullOrEmpty(name))
        {
            name = $"Session {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        session.Name = name;
        session._theme = theme ?? ConsoleTheme.Resolve(null);
        session._pageSize = pageSize;
        session.SessionId = Guid.NewGuid();
        session.StartedAt = DateTime.Now;

        // wire up the on-disk store (creates entries/ and resolves the collision-free file path)
        session._store = new EntryStore(session, name);

        return session;
    }

    // resumes a session from a previously saved snapshot, continuing to write
    // to the same file on disk
    public static Session Resume(SessionSnapshot snapshot, string filePath, int pageSize, ConsoleTheme? theme = null)
    {
        Session session = new();

        session.Name = snapshot.Name ?? "Unnamed session";
        session._theme = theme ?? ConsoleTheme.Resolve(null);
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

    // Phase 1 (Terminal.Gui migration): builds the in-memory state from a snapshot
    // WITHOUT an EntryStore, so no background flush loop exists and nothing can be
    // written to disk from this session.
    // Phase 2 note: both writable --tui flows (new/continue) now build REAL sessions through
    // StartNewForTui/Resume, so nothing calls this today. It is kept deliberately: it is the
    // only way to render a session with a hard guarantee of no writes, which a future
    // read-only "view a previous session" screen needs.
    public static Session LoadReadOnly(SessionSnapshot snapshot, int pageSize = 30, ConsoleTheme? theme = null)
    {
        Session session = new();

        session.Name = snapshot.Name ?? "Unnamed session";
        session._theme = theme ?? ConsoleTheme.Resolve(null);
        session._pageSize = pageSize;
        session.SessionId = snapshot.SessionId;
        session.StartedAt = snapshot.StartedAt;
        session.EndedAt = snapshot.EndedAt;

        // the snapshot file is user-editable, so guard the string fields the display
        // paths assume are non-null (a hand-edited file can deserialize them as null)
        foreach(EntrySnapshot es in snapshot.Entries)
        {
            TimeEntry entry = TimeEntry.FromSnapshot(
                es.Id, es.StartTime, es.EndTime, es.Task ?? "none",
                es.Description ?? string.Empty, es.Logged, es.IsComplete, es.IsDeleted);
            session._timeEntries[entry.Id] = entry;
        }

        // same rule as Resume: an in-progress (non-deleted, incomplete) entry means active
        session.IsActive = session._timeEntries.Values.Any(e => !e.IsDeleted && !e.IsComplete);

        // deliberately no TimeEntry.ReseedId here: a read-only session never mints an
        // ID, and reseeding would mutate process-wide state for a session we do not own

        return session;
    }

    // read-only projections for the Terminal.Gui view (Phase 1). Deleted entries are
    // excluded everywhere, exactly as in the Spectre path.
    // - oldest-first is the order DisplaySummary() walks the dictionary in, so the
    //   summary's task groups appear in the same order in both UIs
    // - newest-first is the order DisplayMainMenu() presents entries in
    internal IReadOnlyList<TimeEntry> VisibleEntriesOldestFirst =>
        _timeEntries.Values.Where(e => !e.IsDeleted).OrderBy(e => e.Id).ToList();

    internal IReadOnlyList<TimeEntry> VisibleEntriesNewestFirst =>
        _timeEntries.Values.Where(e => !e.IsDeleted).OrderByDescending(e => e.Id).ToList();

    internal ConsoleTheme Theme => _theme;

    // -----------------------------------------------------------------------------------
    // Phase 2 (Terminal.Gui migration): the shared action surface.
    // The Spectre flows below keep their prompts and call these same state transitions, so
    // both UIs run one implementation of every guard and mutation.
    // -----------------------------------------------------------------------------------

    // the only place deleted entries are listed: oldest-first, same order as the Spectre flow
    internal IReadOnlyList<TimeEntry> DeletedEntriesOldestFirst =>
        _timeEntries.Values.Where(e => e.IsDeleted).OrderBy(e => e.Id).ToList();

    // distinct tasks that still have unlogged completed work (deleted, in-progress and "none"
    // entries are excluded) - the live choice set behind the "Log a task group" flow
    internal IReadOnlyList<string> UnloggedTaskGroups =>
        _timeEntries.Values
            .Where(entry => !entry.IsDeleted
                         && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase)
                         && entry.IsComplete
                         && !entry.Logged)
            .Select(entry => entry.Task)
            .Distinct()
            .ToList();

    internal int UnloggedEntryCount(string taskGroup) =>
        _timeEntries.Values.Count(entry => !entry.IsDeleted
                                        && entry.Task.Equals(taskGroup, StringComparison.OrdinalIgnoreCase)
                                        && entry.IsComplete
                                        && !entry.Logged);

    internal TimeEntry? FindEntry(int entryId) =>
        _timeEntries.TryGetValue(entryId, out TimeEntry? entry) ? entry : null;

    // the per-entry rules the update flow branches on - the same two conditions the Spectre
    // UpdateEntryFlow tests before the delete confirm and before the logged prompt
    internal bool IsDeletableEntry(int entryId) =>
        _timeEntries.TryGetValue(entryId, out TimeEntry? entry) && entry.IsComplete && !entry.IsDeleted;

    internal bool HasLoggedState(int entryId) =>
        _timeEntries.TryGetValue(entryId, out TimeEntry? entry)
        && entry.IsComplete
        && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase);

    // starts an entry with the task the caller already collected; blank input becomes "none",
    // matching the Spectre StartNewEntry prompt's behavior for an empty answer
    internal void StartNewEntryWithTask(string? task)
    {
        InsertNewEntry(TimeEntry.GetNextEntry(), task);
    }

    // the shared insert+activate transition. the entry (and therefore its StartTime) is
    // created by the caller so the Spectre path can keep stamping the start time BEFORE its
    // prompt, exactly as it always has
    private void InsertNewEntry(TimeEntry newEntry, string? task)
    {
        string trimmedTask = (task ?? string.Empty).Trim();
        if(String.IsNullOrEmpty(trimmedTask)) trimmedTask = "none";

        newEntry.Task = trimmedTask;
        lock(_store.MutationLock)
        {
            _timeEntries[newEntry.Id] = newEntry;
        }
        IsActive = true;
        _store.MarkDirty();
    }

    // applies logged/task/description in ONE atomic block (the Spectre flow's was three
    // separate lock blocks until issue #19's fix; both paths now share this one).
    // logged == null means the entry has no logged state (in-progress or "none" task)
    internal void ApplyEntryUpdate(int entryId, bool? logged, string? task, string? description)
    {
        if(!_timeEntries.TryGetValue(entryId, out TimeEntry? entry)) return;

        lock(_store.MutationLock)
        {
            if(logged.HasValue) entry.Logged = logged.Value;
            entry.Task = (task ?? string.Empty).Trim();
            entry.Description = (description ?? string.Empty).Trim();
        }

        _store.MarkDirty();
    }

    // soft delete: flag the entry instead of removing it from the store, so it stays
    // reachable through "View deleted entries" and survives a restart
    internal bool ApplyEntryDelete(int entryId)
    {
        if(!IsDeletableEntry(entryId)) return false;

        lock(_store.MutationLock)
        {
            _timeEntries[entryId].IsDeleted = true;
        }
        _store.MarkDirty();

        return true;
    }

    internal bool ApplyEntryRestore(int entryId)
    {
        if(!_timeEntries.TryGetValue(entryId, out TimeEntry? entry) || !entry.IsDeleted) return false;

        lock(_store.MutationLock)
        {
            entry.IsDeleted = false;
        }
        _store.MarkDirty();

        return true;
    }

    // logs a whole task group in a single lock acquisition; in-progress entries are skipped
    internal bool ApplyLogTaskGroup(string taskGroup)
    {
        if(String.IsNullOrEmpty(taskGroup)) return false;

        lock(_store.MutationLock)
        {
            foreach(TimeEntry entry in _timeEntries.Values.Where(entry => !entry.IsDeleted && entry.Task.Equals(taskGroup, StringComparison.OrdinalIgnoreCase)))
            {
                if(!entry.IsComplete) continue; // skip in progress entries, only log completed entries
                entry.Logged = true;
            }
        }
        _store.MarkDirty();

        return true;
    }

    // stops the in-progress entry, stamps the end of the session and marks it dirty - with NO
    // console output, so both UIs can do their own teardown afterwards (the Spectre path draws
    // its final tables, the TUI returns to the shell so Terminal.Gui can restore the screen)
    internal void EndSession()
    {
        StopCurrentEntry();

        lock(_store.MutationLock)
        {
            EndedAt = DateTime.Now;
        }
        _store.MarkDirty();
    }

    public void MainLoop()
    {
        while(!_shouldExit)
        {
            AnsiConsole.Clear();
            DisplaySummary();
            DisplayActiveStateBanner();
            DisplayMainMenu();
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
            .BorderColor(_theme.DetailBorder)
            .Title($"[{_theme.HeadingMarkup}]{Markup.Escape(Name)}[/]");

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

            table.AddRow(entry.Id.ToString(), entry.StartTime.ToString("yyyy-MM-dd HH:mm"), entry.IsComplete ? entry.EndTime.ToString("yyyy-MM-dd HH:mm") : $"[{_theme.InProgressMarkup}]In Progress[/]", Markup.Escape(entry.Task), entry.Logged ? "yes" : "no", Markup.Escape(entry.Description));
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
            .BorderColor(_theme.SummaryBorder)
            .Title($"[{_theme.HeadingMarkup}]Summary[/]");

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

            // highlight unlogged time in red for named tasks that still have unlogged
            // work (the "none" pseudo-task and zero-unlogged rows stay plain)
            string unloggedCell = (!emptyTask && taskGroup.UnloggedMins > 0)
                ? $"[{_theme.InactiveColor.ToMarkup()}]{TimeSpan.FromMinutes(taskGroup.UnloggedMins):hh\\:mm}[/]"
                : $"{TimeSpan.FromMinutes(taskGroup.UnloggedMins):hh\\:mm}";

            table.AddRow(Markup.Escape(taskGroup.Task), taskGroup.EntryCount.ToString(), unloggedCell, $"{TimeSpan.FromMinutes(taskGroup.TotalMins):hh\\:mm}");
        }

        AnsiConsole.Write(table);

        // issue #15: totals render as a single line BETWEEN the summary table and the
        // menu/list (not as a row inside the table); excludes entries not part of a task
        RenderTotalsLine(totalUnloggedMins, totalTotalMins);
    }

    private void DisplayActiveStateBanner()
    {
        string label = IsActive ? "ACTIVE" : "NOT ACTIVE";
        Color color = IsActive ? _theme.ActiveColor : _theme.InactiveColor;
        string art = BuildActiveStateArt(label);

        Panel banner = new Panel(new Markup($"[{color.ToMarkup()}]{art}[/]"))
            .Padding(1, 0)
            .BorderColor(color);

        AnsiConsole.Write(banner);
        AnsiConsole.WriteLine();
    }

    // internal (not private) so the Terminal.Gui banner Label renders the same art
    // instead of duplicating the glyph table
    internal static string BuildActiveStateArt(string label)
    {
        Dictionary<char, string[]> font = new()
        {
            ['A'] = new[] { " ### ", "#   #", "#####", "#   #", "#   #" },
            ['C'] = new[] { " ####", "#", "#", "#", " ####" },
            ['E'] = new[] { "#####", "#", "####", "#", "#####" },
            ['I'] = new[] { "#####", "  #", "  #", "  #", "#####" },
            ['N'] = new[] { "#   #", "##  #", "# # #", "#  ##", "#   #" },
            ['O'] = new[] { " ### ", "#   #", "#   #", "#   #", " ### " },
            ['T'] = new[] { "#####", "  #", "  #", "  #", "  #" },
            ['V'] = new[] { "#   #", "#   #", "#   #", " # #", "  #" }
        };

        string[] words = label.Split(' ');
        string[] rows = Enumerable.Range(0, 5)
            .Select(row => string.Join("   ", words.Select(word =>
                string.Join(" ", word.Select(letter => font[letter][row].PadRight(5))))).TrimEnd())
            .ToArray();
        int width = rows.Max(row => row.Length);
        return string.Join(Environment.NewLine, rows.Select(row => row.PadRight(width)));
    }

    // prints e.g. "Total unlogged task time: 01:15   Total time: 02:40" as its own line
    // (trailing blank line separates it from the menu that follows)
    private void RenderTotalsLine(double totalUnloggedMins, double totalTotalMins)
    {
        AnsiConsole.MarkupLine($"[{_theme.TotalsMarkup}]Total unlogged task time:[/] {TimeSpan.FromMinutes(totalUnloggedMins):hh\\:mm}    [{_theme.TotalsMarkup}]Total time:[/] {TimeSpan.FromMinutes(totalTotalMins):hh\\:mm}");
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

        AnsiConsole.MarkupLine($"[{_theme.PromptMarkup}]Select an [{_theme.AccentMarkup}]option[/] or [{_theme.SecondaryMarkup}]entry[/] to update:[/]");

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
                    AnsiConsole.MarkupLine($"[{_theme.ErrorMarkup}]Invalid choice. Press any key to continue...[/]");
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
            -1 => IsActive ? $"[{_theme.AccentMarkup}]Stop current entry and start a new one[/]" : $"[{_theme.AccentMarkup}]Start a new entry[/]",
            -2 => $"[{_theme.AccentMarkup}]Log a task group[/]",
            -3 => $"[{_theme.AccentMarkup}]Stop tracking[/]",
            -4 => $"[{_theme.AccentMarkup}]Stop and exit[/]",
            -5 => $"[{_theme.AccentMarkup}]View deleted entries[/]",
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

        // status column: Logged / Unlogged for completed real tasks, N/A otherwise
        // (in-progress entries and "none"-task entries have no logged/unlogged state).
        // The column is always emitted and padded to a fixed width so the description
        // lines up on every row; markup wraps after the pad so tags stay zero-width.
        bool hasStatus = entry.IsComplete && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase);
        string statusText = hasStatus ? (entry.Logged ? "Logged" : "Unlogged") : "N/A";
        string statusColor = hasStatus
            ? (entry.Logged ? _theme.PositiveMarkup : _theme.InactiveColor.ToMarkup())
            : _theme.MutedMarkup;

        string idPart = $"#{entry.Id}".PadRight(idWidth + 1);
        string taskPart = Markup.Escape(entry.Task).PadRight(taskWidth);

        // pad the PLAIN time text to the fixed width, then wrap in markup so the
        // tags don't count against the pad (markup is zero-width when rendered)
        string timeText = $"{entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "In Progress")}".PadRight(timeWidth);
        string timePart = entry.IsComplete ? timeText : $"[{_theme.InProgressMarkup}]{timeText}[/]";

        string row = $"[{_theme.SecondaryMarkup}]{idPart} | {taskPart} | {timePart} | [{statusColor}]{statusText.PadRight(StatusColumnWidth)}[/] | {(string.IsNullOrEmpty(entry.Description) ? $"[{_theme.MutedMarkup}]No description[/]" : Markup.Escape(entry.Description))}[/]";

        return row;
    }

    private void StartNewEntry()
    {
        TimeEntry newEntry = TimeEntry.GetNextEntry();
        TextPrompt<string> entryTaskPrompt = new TextPrompt<string>("Entry started, enter a task if desired:")
            .AllowEmpty()
            .ShowDefaultValue(false);
        string entryTask = AnsiConsole.Prompt(entryTaskPrompt);

        // the shared transition applies the "blank means none" rule (StartNewEntryWithTask)
        InsertNewEntry(newEntry, entryTask);
    }

    internal void StopCurrentEntry()
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

    private void UpdateEntryFlow(int entryId)
    {
        TimeEntry? selectedEntry;
        if(!_timeEntries.TryGetValue(entryId, out selectedEntry))
        {
            AnsiConsole.MarkupLine($"[{_theme.ErrorMarkup}]Selected entry not found.[/]");
            return;
        }

        // check if prompt was cancelled by checking if the returned TimeEntry is invalid
        if(!selectedEntry.IsValid) return;

        // issue #12: deletion lives inside the edit entry view. only completed,
        // non-deleted entries can be deleted - soft delete sets the IsDeleted flag
        // (the entry stays in the store and is only reachable via "View deleted entries")
        if(IsDeletableEntry(selectedEntry.Id)
           && AnsiConsole.Confirm($"Delete this entry? (id {selectedEntry.Id}, task '{Markup.Escape(selectedEntry.Task)}')", defaultValue: false))
        {
            ApplyEntryDelete(selectedEntry.Id);
            return;
        }

        // gather ALL prompt inputs first, then apply them in a single atomic
        // mutation under the lock (was three separate lock blocks - the entry
        // update can no longer be persisted half-applied by a mid-flow flush)
        bool? updatedLogged = null;
        if(HasLoggedState(selectedEntry.Id))
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

        // one atomic apply for all three fields (shared with the Terminal.Gui dialog flow)
        ApplyEntryUpdate(selectedEntry.Id, updatedLogged, updatedEntryTask, updatedEntryDescription);
    }

    private void LogTaskGroupFlow()
    {
        // get distinct task groups from entries that still have unlogged, completed work
        // (deleted, in-progress and "none" entries don't count) - the shared live projection
        IReadOnlyList<string> taskGroups = UnloggedTaskGroups;

        if(taskGroups.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{_theme.ErrorMarkup}]No task groups with unlogged entries to log. Press any key to continue...[/]");
            AnsiConsole.Console.Input.ReadKey(true);
            return;
        }

        SelectionPrompt<string> taskGroupPrompt = new SelectionPrompt<string>()
            .Title("Select a task group to log (press ESC to cancel):")
            .AddChoices(taskGroups)
            .UseConverter(taskGroup => $"{Markup.Escape(taskGroup)} ({UnloggedEntryCount(taskGroup)} unlogged)");

        taskGroupPrompt.CancelResult = () => string.Empty;

        string selectedTaskGroup = taskGroupPrompt.Show(AnsiConsole.Console);

        if(string.IsNullOrEmpty(selectedTaskGroup)) return;

        // one lock acquisition for the whole group (shared with the Terminal.Gui dialog flow)
        ApplyLogTaskGroup(selectedTaskGroup);
    }

    private void DeleteEntryFlow()
    {
        // only completed entries can be deleted, and already-deleted ones are not shown again
        IEnumerable<TimeEntry> deletableEntries = _timeEntries.Values.Where(entry => entry.IsComplete && !entry.IsDeleted);
        if(!deletableEntries.Any())
        {
            AnsiConsole.MarkupLine($"[{_theme.ErrorMarkup}]No completed entries to delete.[/]");
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
        ApplyEntryDelete(selectedEntry.Id);
    }

    // the only place deleted entries are viewable: lists them oldest-first and
    // restores the selected one by flipping its IsDeleted flag back to false
    private void ViewDeletedEntriesFlow()
    {
        IReadOnlyList<TimeEntry> deletedEntries = DeletedEntriesOldestFirst;
        if(deletedEntries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{_theme.ErrorMarkup}]No deleted entries. Press any key to continue...[/]");
            AnsiConsole.Console.Input.ReadKey(true);
            return;
        }

        SelectionPrompt<TimeEntry> entryPrompt = new SelectionPrompt<TimeEntry>()
            .Title("Select a deleted entry to restore (press ESC to cancel):")
            .AddChoices(deletedEntries)
            .UseConverter(entry => $"Id: {entry.Id} {Markup.Escape(entry.Task)} ({entry.StartTime:yyyy-MM-dd HH:mm} - {entry.EndTime.ToString("yyyy-MM-dd HH:mm")}) [{_theme.DeletedMarkup}](deleted)[/]");

        entryPrompt.CancelResult = () => TimeEntry.GetEmpty(); // this will return an empty (invalid) entry to check against

        TimeEntry selectedEntry = entryPrompt.Show(AnsiConsole.Console);

        // check if prompt was cancelled by checking if the returned TimeEntry is invalid
        if(!selectedEntry.IsValid) return;

        if(!AnsiConsole.Confirm("Restore this entry?", defaultValue: true))
        {
            return;
        }

        // flips IsDeleted back to false (shared with the Terminal.Gui dialog flow)
        ApplyEntryRestore(selectedEntry.Id);
    }

    // IN PROGRESS
    private void StopSession(bool exit = false)
    {
        if(!exit)
        {
            StopCurrentEntry();
            return;
        }

        // stops the in-progress entry, stamps EndedAt and marks the state dirty - all state,
        // no output (shared with the Terminal.Gui stop-and-exit path)
        EndSession();

        // the Spectre path's final render, then unwind MainLoop so Program.Main can perform
        // the last on-disk flush before the process exits normally
        AnsiConsole.Clear();
        DisplayEntries();
        DisplaySummary();
        _shouldExit = true;
        // Environment.Exit(0); // replaced by the graceful unwind above so the final flush can run
    }
}
