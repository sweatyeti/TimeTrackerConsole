using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui view for the migration.
// Phase 0 opened an empty shell; Phase 1 filled it with read-only regions (banner, summary,
// totals, entry list, status bar) driven by Session.LoadReadOnly.
// Phase 2 makes it WRITABLE: both entry points build a real Session (StartNewForTui /
// Resume), so the existing EntryStore background flush writes the session JSON exactly as
// the Spectre path does, and every flow (start/stop, update, soft delete, restore, log a
// task group, stop tracking, stop and exit) runs through the same Session transitions as
// the Spectre path - only the prompts differ (dialogs instead of Spectre prompts).
//
// Phase 3: every colour comes from TuiSchemes, which registers the ConsoleTheme roles as named
// schemes with SchemeManager and hands each view its scheme through View.SchemeName. No view
// carries an inline colour, and the TUI still reads the same role table the Spectre path does.
//
// Terminal.Gui owns the screen here: ConsoleBackdrop is deliberately never applied from this
// path, and the screen is restored by Terminal.Gui before the final flush runs.
internal sealed class TuiSessionWindow
{
    private readonly IApplication _app;
    private readonly Session _session;
    private readonly TuiSchemes _schemes;

    private Window? _window;
    private int _selectedEntryId = -1;
    private bool _exitRequested;

    private TuiSessionWindow(IApplication app, Session session)
    {
        _app = app;
        _session = session;
        _schemes = new TuiSchemes(session.Theme);
    }

    // `new --tui`: creates a real, writable session (EntryStore + background flush) and opens
    // the main window. The first entry's task is collected in a dialog first - the Spectre
    // path asks for it inline as part of starting the session.
    public static void RunNew(string? name, int pageSize, ConsoleTheme theme)
    {
        using IApplication app = Application.Create();
        app.Init();

        Session session = Session.StartNewForTui(name, pageSize, theme);

        try
        {
            TuiSchemes schemes = new(session.Theme);
            string? firstTask = EntryDialogs.PromptForText(
                app, schemes, "New entry", "Entry started, enter a task if desired:", string.Empty);

            // Esc on the first prompt has no Spectre equivalent (its prompt cannot be
            // cancelled), so it falls back to the same "none" task an empty answer gives
            session.StartNewEntryWithTask(firstTask ?? string.Empty);

            new TuiSessionWindow(app, session).RunWindow();
        }
        finally
        {
            // Terminal.Gui restores the terminal when the application is disposed; the final
            // session flush runs afterwards, so nothing is written after the screen is handed
            // back to the shell
            app.Dispose();
            session.Shutdown();
        }
    }

    // `continue --tui`: lists every saved session (newest-first, exactly like the Spectre
    // flow) and resumes the chosen one in place. Returns the process exit code.
    public static int RunContinue(int pageSize, ConsoleTheme theme)
    {
        using IApplication app = Application.Create();
        app.Init();

        List<(SessionSnapshot Snapshot, string FilePath)> sessions = EntryStore.ListAllSessions();

        if(sessions.Count == 0)
        {
            EntryDialogs.ShowMessage(app, "Continue", "No previous sessions found.");
            return 0;
        }

        TuiSchemes schemes = new(theme);
        int? choice = EntryDialogs.SelectFromList(
            app, schemes, "Continue", "Select a session to resume (press ESC to cancel):", SessionLabels(sessions));

        if(choice is null) return 0;

        (SessionSnapshot snapshot, string filePath) = sessions[choice.Value];
        Session session = Session.Resume(snapshot, filePath, pageSize, theme);

        try
        {
            new TuiSessionWindow(app, session).RunWindow();
        }
        finally
        {
            app.Dispose();
            session.Shutdown();
        }

        return 0;
    }

    // the same labels the Spectre continue prompt prints (index, name, start, unfinished marker)
    private static IReadOnlyList<string> SessionLabels(List<(SessionSnapshot Snapshot, string FilePath)> sessions)
    {
        List<string> labels = new(sessions.Count);
        for(int i = 0; i < sessions.Count; i++)
        {
            (SessionSnapshot snap, _) = sessions[i];
            string status = snap.EndedAt is null ? " (unfinished)" : string.Empty;
            labels.Add($"{i + 1}. {snap.Name ?? "Unnamed session"} - {snap.StartedAt:yyyy-MM-dd HH:mm}{status}");
        }

        return labels;
    }

    private void RunWindow()
    {
        Window window = new()
        {
            Title = $"TimeTracker - {_session.Name}",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = _schemes.BaseName
        };
        _window = window;

        // F2..F6 are the admin options of the Spectre main menu, in the same order and with the
        // same behavior; Esc quits via the runnable's own default key (and by clicking the
        // shortcut), which leaves the session unfinished exactly like Ctrl+C on the Spectre path
        StatusBar statusBar = new(new List<Shortcut>
        {
            new(Key.F2, "Stop/start", StartOrStopEntry, null) { BindKeyToApplication = true },
            new(Key.F3, "Log group", LogTaskGroupFlow, null) { BindKeyToApplication = true },
            new(Key.F4, "Deleted", ViewDeletedFlow, null) { BindKeyToApplication = true },
            new(Key.F5, "Stop tracking", StopTracking, null) { BindKeyToApplication = true },
            new(Key.F6, "Stop+exit", StopAndExit, null) { BindKeyToApplication = true },
            new(Key.Esc, "Quit", RequestExit, null)
        })
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            SchemeName = _schemes.BaseName
        };

        // the status bar is part of the rebuilt body, so it is added by BuildBody
        _statusBar = statusBar;

        BuildBody();

        using(window)
        {
            _app.Run(window);
        }

        _window = null;
    }

    private StatusBar? _statusBar;

    // rebuilds every region from the LIVE session state. Called once when the window opens and
    // again after every action, so the banner, summary, totals and entry list always show the
    // entry set that was just mutated - the TUI equivalent of the Spectre loop's clear+redraw.
    private void BuildBody()
    {
        if(_window is null) return;

        foreach(View view in _window.RemoveAll())
        {
            view.Dispose();
        }

        bool isActive = _session.IsActive;
        // a wider letter/word gap than the Spectre art, so the banner reads as a banner in a
        // full-width window rather than a narrow strip pinned to the left
        string art = Session.BuildActiveStateArt(isActive ? "ACTIVE" : "NOT ACTIVE", letterGap: 3, wordGap: 5);

        Label banner = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = ArtHeightInLines(art),
            Text = art,
            TextAlignment = Alignment.Center,
            SchemeName = isActive ? _schemes.BannerActiveName : _schemes.BannerInactiveName
        };
        _window.Add(banner);

        View previous = banner;

        // the Spectre path skips the whole summary section when there are no entries
        if(_session.EntryCount > 0)
        {
            SummaryTableSource summarySource = new(_session.VisibleEntriesOldestFirst, _schemes);

            TableStyle summaryStyle = new()
            {
                ShowHeaders = true,
                ShowHorizontalHeaderOverline = false,
                ShowHorizontalHeaderUnderline = true,
                ShowHorizontalBottomLine = true,
                ShowVerticalCellLines = true,
                ExpandLastColumn = false,
                HeaderScheme = _schemes.SummaryHeaderScheme,
                RowColorGetter = summarySource.RowColorGetter
            };

            TableView summary = new()
            {
                X = 0,
                Y = Pos.Bottom(previous) + 1,
                Width = Dim.Fill(),
                Height = summaryStyle.ShowHeaders
                    ? summarySource.Rows + 3 // header row + header rule + bottom line
                    : summarySource.Rows + 1,
                Table = summarySource,
                Style = summaryStyle,
                SchemeName = _schemes.BaseName
            };
            _window.Add(summary);
            previous = summary;

            Label totals = new()
            {
                X = 0,
                Y = Pos.Bottom(previous) + 1,
                Width = Dim.Fill(),
                Height = 1,
                Text = $"Total unlogged task time: {FormatMinutes(summarySource.TotalUnloggedMins)}    Total time: {FormatMinutes(summarySource.TotalTotalMins)}",
                SchemeName = _schemes.TotalsName
            };
            _window.Add(totals);
            previous = totals;
        }

        TimeEntry[] visible = _session.VisibleEntriesNewestFirst.ToArray();
        EntryListDataSource entrySource = new(visible, _schemes);
        ListView entries = new()
        {
            X = 0,
            Y = Pos.Bottom(previous) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Source = entrySource,
            SchemeName = _schemes.BaseName
        };

        // Enter on the entry list opens the update flow - the same entry the Spectre menu
        // selection opens (its list order is newest-first, like DisplayMainMenu's choices)
        entries.Accepting += (_, args) =>
        {
            args.Handled = true;
            UpdateSelectedEntry(entries.SelectedItem ?? -1);
        };

        if(entrySource.Count > 0)
        {
            entries.SelectedItem = RestoredSelectionIndex(visible);
        }
        _window.Add(entries);

        if(_statusBar is not null)
        {
            _statusBar.Y = Pos.AnchorEnd(1);
            _window.Add(_statusBar);
        }

        entries.SetFocus();
    }

    // the art is a fixed 5-row block; deriving the height from the string keeps the glyph
    // table in Session as the single source of truth
    private static int ArtHeightInLines(string art) => art.Count(character => character == '\n') + 1;

    private static string FormatMinutes(double minutes) => $"{TimeSpan.FromMinutes(minutes):hh\\:mm}";

    // keeps the highlighted row on the same entry across a rebuild where possible; new entries
    // (which sort to the top) fall back to the first row, as DisplayMainMenu does
    private int RestoredSelectionIndex(IReadOnlyList<TimeEntry> visible)
    {
        for(int i = 0; i < visible.Count; i++)
        {
            if(visible[i].Id == _selectedEntryId) return i;
        }

        return 0;
    }

    // an application-bound shortcut (F2..F6) also fires while one of our dialog runnables is on
    // top; an action must only ever run against the main window, or it would nest a dialog
    // inside a dialog and mutate state from under the open prompt
    private bool MainWindowIsTop => _window is not null && ReferenceEquals(_app.TopRunnable, _window);

    // T2.1: the Spectre menu's "stop current entry and start a new one" / "start a new entry"
    // option. The task is asked for first, so a cancelled (ESC) dialog leaves the session
    // exactly as it was. Blank input becomes "none", as in the Spectre prompt.
    private void StartOrStopEntry()
    {
        if(!MainWindowIsTop) return;

        string? task = EntryDialogs.PromptForText(
            _app, _schemes, "New entry", "Entry started, enter a task if desired:", string.Empty);

        if(task is null) return; // ESC cancels before anything is stopped or started

        _session.StopCurrentEntry(); // no-op when nothing is in progress
        _session.StartNewEntryWithTask(task);
        Refresh();
    }

    // T2.3/T2.4: what the Spectre path runs when an entry is selected in the main menu. For a
    // deletable entry it first offers the soft delete (default: no, as AnsiConsole.Confirm
    // defaultValue: false), otherwise it opens the update dialog, whose logged check box only
    // appears for completed non-"none" entries - the same rule the Spectre flow applies.
    private void UpdateSelectedEntry(int index)
    {
        IReadOnlyList<TimeEntry> visible = _session.VisibleEntriesNewestFirst;
        if(index < 0 || index >= visible.Count) return;

        _selectedEntryId = visible[index].Id;

        if(_session.IsDeletableEntry(_selectedEntryId))
        {
            bool delete = EntryDialogs.Confirm(
                _app,
                "Delete entry",
                $"Delete this entry? (id {_selectedEntryId}, task '{visible[index].Task}')",
                "Delete",
                "Cancel",
                defaultIsAffirmative: false); // AnsiConsole.Confirm(defaultValue: false)

            if(delete)
            {
                _session.ApplyEntryDelete(_selectedEntryId);
                Refresh();
                return;
            }
        }

        // re-read through the session (the row snapshot is only for display and its id)
        TimeEntry? current = _session.FindEntry(_selectedEntryId);
        if(current is null || !current.IsValid) return;

        // the logged field is only offered for completed entries with a real task
        bool showLogged = _session.HasLoggedState(current.Id);

        EntryDialogs.EntryUpdateResult? update = EntryDialogs.PromptForEntryUpdate(_app, _schemes, current, showLogged);
        if(update is null) return; // ESC cancels, nothing is written

        EntryDialogs.EntryUpdateResult result = update.Value;

        // the Spectre prompts return their default (the current value) for an empty answer, so
        // an empty field means "leave it as it is" rather than "clear it"
        string task = string.IsNullOrEmpty(result.Task) ? current.Task : result.Task;
        string description = string.IsNullOrEmpty(result.Description) ? current.Description : result.Description;

        _session.ApplyEntryUpdate(current.Id, result.Logged, task, description);
        Refresh();
    }

    // T2.5: log a whole task group - the distinct unlogged completed tasks, labelled with their
    // unlogged count exactly as the Spectre SelectionPrompt labels them
    private void LogTaskGroupFlow()
    {
        if(!MainWindowIsTop) return;

        IReadOnlyList<string> taskGroups = _session.UnloggedTaskGroups;
        if(taskGroups.Count == 0)
        {
            EntryDialogs.ShowMessage(_app, "Log a task group", "No task groups with unlogged entries to log.");
            return;
        }

        List<string> labels = taskGroups
            .Select(taskGroup => $"{taskGroup} ({_session.UnloggedEntryCount(taskGroup)} unlogged)")
            .ToList();

        int? choice = EntryDialogs.SelectFromList(
            _app, _schemes, "Log a task group", "Select a task group to log (press ESC to cancel):", labels);

        if(choice is null) return;

        _session.ApplyLogTaskGroup(taskGroups[choice.Value]);
        Refresh();
    }

    // T2.6: the only place deleted entries are reachable - list them oldest-first with the
    // "(deleted)" marker and restore the chosen one (confirmation, as on the Spectre path)
    private void ViewDeletedFlow()
    {
        if(!MainWindowIsTop) return;

        IReadOnlyList<TimeEntry> deleted = _session.DeletedEntriesOldestFirst;
        if(deleted.Count == 0)
        {
            EntryDialogs.ShowMessage(_app, "Deleted entries", "No deleted entries.");
            return;
        }

        List<string> labels = deleted
            .Select(entry => $"Id: {entry.Id} {entry.Task} ({entry.StartTime:yyyy-MM-dd HH:mm} - {entry.EndTime:yyyy-MM-dd HH:mm}) (deleted)")
            .ToList();

        int? choice = EntryDialogs.SelectFromList(
            _app, _schemes, "Deleted entries", "Select a deleted entry to restore (press ESC to cancel):", labels);

        if(choice is null) return;

        if(!EntryDialogs.Confirm(_app, "Restore entry", "Restore this entry?", "Restore", "Cancel", defaultIsAffirmative: true))
        {
            return; // AnsiConsole.Confirm(defaultValue: true)
        }

        _session.ApplyEntryRestore(deleted[choice.Value].Id);
        Refresh();
    }

    // T2.2: "stop tracking" - stop the in-progress entry and stay in the session
    private void StopTracking()
    {
        if(!MainWindowIsTop) return;

        _session.StopCurrentEntry();
        Refresh();
    }

    // T2.2: "stop and exit" - end the session (stop the entry, stamp EndedAt, mark dirty) and
    // unwind the app; the flush runs in RunNew/RunContinue after Terminal.Gui has restored the
    // terminal, which is why this only requests the stop
    private void StopAndExit()
    {
        if(!MainWindowIsTop) return;

        _session.EndSession();
        ExitNow();
    }

    // Esc quits WITHOUT ending the session (it stays open for `continue`), and it is easy to
    // hit by accident, so it asks first. F6 ("Stop+exit") is a deliberate two-part action and
    // stays direct - it is the normal way to close out a session.
    private void RequestExit()
    {
        if(_exitRequested || !MainWindowIsTop) return;

        // The confirm has to run after this key event finishes unwinding. Opening the dialog
        // inline let the very same Esc that triggered it cancel the dialog immediately, so Esc
        // looked like it did nothing at all.
        _app.Invoke(() =>
        {
            if(_exitRequested || !MainWindowIsTop) return;

            if(!EntryDialogs.Confirm(_app, "Quit",
                "Exit TimeTracker? This session stays open and can be resumed with 'continue'.",
                "Quit", "Keep working", defaultIsAffirmative: false))
            {
                return;
            }

            ExitNow();
        });
    }

    // the actual unwind, shared by both exit paths once they have decided to go
    private void ExitNow()
    {
        if(_exitRequested) return;

        _exitRequested = true;
        _app.RequestStop();
    }

    // redraw every region from the mutated session state (the TUI's clear+repaint)
    private void Refresh()
    {
        BuildBody();
        _window?.SetNeedsLayout();
        _window?.SetNeedsDraw();
    }
}
