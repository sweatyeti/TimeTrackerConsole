using System;
using System.Collections.Generic;
using System.Drawing;
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
    private bool _exitRequested;

    // the entry the NEXT refresh must focus, whatever the current row is: set by an action that
    // knows which entry it just created (see StartOrStopEntry), consumed by Refresh. Null means
    // "keep the row the user is on".
    private int? _pendingSelectionEntryId;

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
        // Checked BEFORE the driver is created. The Spectre path reports an empty list as a
        // line of text rather than a dialog, and a MessageBox is the wrong tool here anyway:
        // run as the app's very first runnable its message label auto-sizes against a zero-width
        // superview and the whole dialog dies with "width ('-3') must be a non-negative value".
        SessionFileListing listing = EntryStore.ListAllSessions();
        List<(SessionSnapshot Snapshot, string FilePath)> sessions = listing.Sessions;

        if(sessions.Count == 0)
        {
            ReportSkippedSessionFiles(listing, theme);

            // fully qualified: this file deliberately imports Terminal.Gui's Color/Attribute
            // namespaces, and a plain `using Spectre.Console` would collide with them
            Spectre.Console.AnsiConsole.MarkupLine($"[{theme.ErrorMarkup}]No previous sessions found.[/]");
            return 0;
        }

        int exitCode = 0;
        Session? session = null;

        using(IApplication app = Application.Create())
        {
            app.Init();

            TuiSchemes schemes = new(theme);
            int? choice = EntryDialogs.SelectFromList(
                app, schemes, "Continue", "Select a session to resume (press ESC to cancel):", SessionLabels(sessions));

            if(choice is not null)
            {
                (SessionSnapshot snapshot, string filePath) = sessions[choice.Value];
                session = Session.Resume(snapshot, filePath, pageSize, theme);

                new TuiSessionWindow(app, session).RunWindow();
            }
        }

        // the final flush runs AFTER the driver has restored the terminal (same order the RunNew
        // path documents), and the skipped-file report comes after that so it is actually visible:
        // an unreadable session file is otherwise indistinguishable from a deleted one
        session?.Shutdown();
        ReportSkippedSessionFiles(listing, theme);

        return exitCode;
    }

    // Mirrors Program.ReportSkippedSessionFiles: names the entries/*.json files that could not be
    // offered, with the reason, and never touches them.
    private static void ReportSkippedSessionFiles(SessionFileListing listing, ConsoleTheme theme)
    {
        foreach(SkippedSessionFile skipped in listing.Skipped)
        {
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[{theme.ErrorMarkup}]Skipped {Spectre.Console.Markup.Escape(System.IO.Path.GetFileName(skipped.FilePath))}: {Spectre.Console.Markup.Escape(skipped.Reason)}.[/]");
        }
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
        MainWindow window = new()
        {
            Title = $"TimeTracker - {_session.Name}",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = _schemes.BaseName
        };
        _window = window;

        // Esc is handled by MainWindow (see Tui/MainWindow.cs): the window declares Command.Quit
        // and reports it handled, so the application-scoped Esc -> Quit binding never runs and the
        // window cannot be closed with Esc. F6 is the way out.

        // F2..F6 are the admin options of the Spectre main menu, in the same order and with the
        // same behavior.
        _shortcuts = new[]
        {
            new Shortcut(Key.F2, FullShortcutTitles[0], StartOrStopEntry, null) { BindKeyToApplication = true },
            new Shortcut(Key.F3, FullShortcutTitles[1], LogTaskGroupFlow, null) { BindKeyToApplication = true },
            new Shortcut(Key.F4, FullShortcutTitles[2], ViewDeletedFlow, null) { BindKeyToApplication = true },
            new Shortcut(Key.F5, FullShortcutTitles[3], StopTracking, null) { BindKeyToApplication = true },
            new Shortcut(Key.F6, FullShortcutTitles[4], StopAndExit, null) { BindKeyToApplication = true }
        };

        StatusBar statusBar = new(_shortcuts)
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            SchemeName = _schemes.BaseName
        };

        // the status bar is added by BuildOnce and lives for the life of the window
        _statusBar = statusBar;

        // resize support: the banner spacing, the status-bar titles and the summary/viewport budget
        // all depend on the window size, and the window owns that state. Re-running Refresh on a
        // viewport change re-applies them; it converges (Refresh does not change the window's own
        // viewport), so this cannot loop.
        window.ViewportChanged += (_, _) => Refresh();

        BuildOnce();

        using(window)
        {
            _app.Run(window);
        }

        _window = null;
    }

    private StatusBar? _statusBar;

    // the F2..F6 shortcuts, kept so the titles can be shortened on a narrow terminal (the bar clips
    // from the right, and F6 is the only way out of the window)
    private Shortcut[] _shortcuts = Array.Empty<Shortcut>();

    // normal widths vs. a terminal narrow enough that the full titles would clip the bar
    private static readonly string[] FullShortcutTitles = { "Stop/start", "Log group", "Deleted", "Stop tracking", "Stop+exit" };
    private static readonly string[] CompactShortcutTitles = { "Start/stop", "Log", "Deleted", "Stop", "Stop+exit" };

    // region fields: each region is built ONCE and lives for the life of the window. A refresh
    // mutates these views and their data sources in place (see Refresh) rather than tearing the
    // tree down and rebuilding it - which is the documented update path: SetNeedsDraw for content
    // changes, SetNeedsLayout when geometry changes. Recreating views instead discards per-view
    // state (focus, scroll offset, adornments, key bindings) and forced the workarounds this
    // refactor removes.
    private Label? _banner;
    private TableView? _summary;
    private Label? _totals;
    private TableView? _entries;
    private EntryTableSource? _entrySource;
    private SummaryTableSource? _summarySource;

    // builds every region once, then hands over to Refresh for the first paint
    private void BuildOnce()
    {
        if(_window is null) return;

        string art = Session.BuildActiveStateArt("ACTIVE", letterGap: 3, wordGap: 5);

        _banner = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = ArtHeightInLines(art),
            Text = art,
            TextAlignment = Alignment.Center
        };
        _window.Add(_banner);

        _summarySource = new SummaryTableSource(_session.VisibleEntriesOldestFirst, _schemes);
        _summary = new TableView
        {
            X = 0,
            Y = Pos.Bottom(_banner) + 1,
            Width = Dim.Fill(),
            Height = 1, // height AND visibility are both settled by the first Refresh
            Table = _summarySource,
            Style = new TableStyle
            {
                ShowHeaders = true,
                ShowHorizontalHeaderOverline = false,
                ShowHorizontalHeaderUnderline = true,
                ShowHorizontalBottomLine = true,
                ShowVerticalCellLines = true,
                ExpandLastColumn = false,
                HeaderScheme = _schemes.SummaryHeaderScheme,
                RowColorGetter = _summarySource.RowColorGetter
            },
            SchemeName = _schemes.BaseName
        };
        _window.Add(_summary);

        _totals = new Label
        {
            X = 0,
            Y = Pos.Bottom(_summary) + 1,
            Width = Dim.Fill(),
            Height = 1,
            // the totals line carries the same emphasis role the Spectre path renders it in
            // (TotalsMarkup) - the scheme existed but was never assigned to this label
            SchemeName = _schemes.TotalsName
        };
        _window.Add(_totals);

        _entrySource = new EntryTableSource(_session.VisibleEntriesNewestFirst);
        _entries = new TableView
        {
            X = 0,
            Y = Pos.Bottom(_totals) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Table = _entrySource,
            Style = EntryTableStyle(),
            FullRowSelect = true,
            MultiSelect = false,
            SchemeName = _schemes.BaseName
        };

        // Enter on the entry list opens the update flow - the same entry the Spectre menu
        // selection opens (its list order is newest-first, like DisplayMainMenu's choices)
        _entries.Accepting += (_, args) =>
        {
            args.Handled = true;
            UpdateSelectedEntry(SelectedEntryRow());
        };
        _window.Add(_entries);

        if(_statusBar is not null)
        {
            _statusBar.Y = Pos.AnchorEnd(1);
            _window.Add(_statusBar);
        }

        // focus is set once, here. The framework restores it after a dialog closes, so a refresh
        // must not re-assert it - that was one of the workarounds the teardown-and-rebuild forced.
        _entries.SetFocus();

        Refresh();
    }

    // the art is a fixed 5-row block; deriving the height from the string keeps the glyph
    // table in Session as the single source of truth
    private static int ArtHeightInLines(string art) => art.Count(character => character == '\n') + 1;

    // The entry table's look and per-cell colours. No headers and no horizontal rules: the entry
    // list is a list, not a captioned grid. The vertical lines are what delimit the columns now
    // that the old " | " separators are gone.
    private TableStyle EntryTableStyle() => new()
    {
        ShowHeaders = false,
        ShowHorizontalHeaderOverline = false,
        ShowHorizontalHeaderUnderline = false,
        ShowHorizontalBottomLine = false,
        ShowVerticalCellLines = true,
        ShowVerticalCellLineForFirstColumn = true,
        ShowVerticalCellLineForLastColumn = false,
        ExpandLastColumn = false,
        ColumnStyles = new Dictionary<int, ColumnStyle>
        {
            [0] = new ColumnStyle { ColorGetter = _ => _schemes.CellSecondaryScheme },
            [1] = new ColumnStyle { ColorGetter = _ => _schemes.CellPlainScheme },
            [2] = new ColumnStyle { ColorGetter = args => TimeScheme(args.RowIndex) },
            [3] = new ColumnStyle { ColorGetter = args => StatusScheme(args.RowIndex) },
            [4] = new ColumnStyle { ColorGetter = args => DescriptionScheme(args.RowIndex) }
        }
    };

    // in-progress entries show their time range in the in-progress colour, everything else plain -
    // the same rule the hand-rolled row builder applied to the time span
    private Scheme TimeScheme(int row)
        => _entrySource?.EntryAt(row) is { IsComplete: false } ? _schemes.CellInProgressScheme : _schemes.CellPlainScheme;

    // Logged / Unlogged / N/A, colour-coded exactly as the old status span was
    private Scheme StatusScheme(int row)
    {
        TimeEntry? entry = _entrySource?.EntryAt(row);
        if(entry is null || !EntryTableSource.HasLoggedState(entry)) return _schemes.CellMutedScheme;

        return entry.Logged ? _schemes.CellPositiveScheme : _schemes.CellUnloggedScheme;
    }

    private Scheme DescriptionScheme(int row)
        => string.IsNullOrEmpty(_entrySource?.EntryAt(row)?.Description)
            ? _schemes.CellMutedScheme
            : _schemes.CellPlainScheme;

    // The table's selection is Value (TableSelection.SelectedCell); Cursor is the terminal caret's
    // position, not the selected row - reading it sent Enter to row 0 regardless of the highlight.
    private int SelectedEntryRow() => _entries?.Value?.SelectedCell.Y ?? -1;

    // The id of the entry the user is actually on, resolved through the CURRENT source - which is
    // why Refresh has to read it BEFORE it replaces the source contents.
    private int CurrentRowEntryId() => _entrySource?.EntryAt(SelectedEntryRow())?.Id ?? -1;

    private static string FormatMinutes(double minutes) => $"{TimeSpan.FromMinutes(minutes):hh\\:mm}";

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

        // Explicit outcome, not a fallback: the new entry is what the user just acted on, so the
        // list must highlight it. (The console menu cannot express this - its first selectable row
        // is an admin option - so this is the TUI's documented choice: the new entry is focused and
        // is the newest row, i.e. row 0.)
        _pendingSelectionEntryId = _session.StartNewEntryWithTask(task);
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

        // the row's entry, for this flow: the row index is only valid against the list it came from,
        // so nothing but the id is carried past this point
        int entryId = visible[index].Id;

        if(_session.IsDeletableEntry(entryId))
        {
            bool delete = EntryDialogs.Confirm(
                _app,
                "Delete entry",
                $"Delete this entry? (id {entryId}, task '{visible[index].Task}')",
                "Delete",
                "Cancel",
                defaultIsAffirmative: false); // AnsiConsole.Confirm(defaultValue: false)

            if(delete)
            {
                _session.ApplyEntryDelete(entryId);
                Refresh();
                return;
            }
        }

        // re-read through the session (the row snapshot is only for display and its id)
        TimeEntry? current = _session.FindEntry(entryId);
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

    // The single exit path. F6 ("Stop+exit") ends the session and then leaves; Esc no longer
    // quits at all, so there is no second caller and nothing left to confirm.
    private void ExitNow()
    {
        if(_exitRequested) return;

        _exitRequested = true;
        _app.RequestStop();
    }

    // redraw every region from the mutated session state (the TUI's clear+repaint)
    // pushes the live session state into the long-lived views. Called once when the window opens
    // and again after every action, so the banner, summary, totals and entry list always show the
    // entry set that was just mutated - the TUI equivalent of the Spectre loop's clear+redraw.
    private void Refresh()
    {
        if(_window is null || _banner is null || _summary is null || _totals is null
            || _entries is null || _entrySource is null || _summarySource is null)
        {
            return;
        }

        // 1. Selection: capture the id of the entry the user is actually on BEFORE the source is
        // replaced. A row index only means something against the content it was measured on, so
        // reading it after the swap (or ignoring it and defaulting to row 0) reset the user's
        // Up/Down navigation on every F-key refresh.
        int selectedId = CurrentRowEntryId();
        int? pendingSelectionId = _pendingSelectionEntryId;
        _pendingSelectionEntryId = null;
        int? focusId = pendingSelectionId ?? (selectedId >= 0 ? selectedId : null);

        // 2. Banner: the block is built at the widest spacing that fits the window, so it is not
        // clipped into nonsense on a narrow terminal; the height is fixed (see ArtHeightInLines) so
        // the rest of the layout does not move with it.
        bool isActive = _session.IsActive;
        _banner.Text = Session.BuildActiveStateArt(isActive ? "ACTIVE" : "NOT ACTIVE", letterGap: 3, wordGap: 5, maxWidth: _window.Viewport.Width);
        _banner.SchemeName = isActive ? _schemes.BannerActiveName : _schemes.BannerInactiveName;

        // 3. Content: both tables project from the same session state; the summary skips the whole
        // section when there are no (non-deleted) entries, exactly as the Spectre path does.
        IReadOnlyList<TimeEntry> visibleNewestFirst = _session.VisibleEntriesNewestFirst;
        bool hasEntries = _session.EntryCount > 0;

        _summarySource.Update(_session.VisibleEntriesOldestFirst);
        _entrySource.Update(visibleNewestFirst);

        // 4. Geometry: the summary is capped against the actual window height so the totals line,
        // the entry table and the status bar keep usable rows (0 = collapsed, e.g. before the first
        // layout pass or on a window too small for the summary chrome).
        int summaryHeight = hasEntries
            ? ViewportBudget.SummaryHeight(_window.Viewport.Height, _summarySource.Rows, visibleNewestFirst.Count > 0)
            : 0;
        bool showSummary = summaryHeight > 0;

        _summary.Height = summaryHeight;
        _summary.Visible = showSummary;

        // the totals line is not tied to the summary's own height: it stays visible when the budget
        // collapses the summary, which is exactly the case where the numbers matter most
        _totals.Height = hasEntries ? 1 : 0;
        _totals.Visible = hasEntries;
        _totals.Text = $"Total unlogged task time: {FormatMinutes(_summarySource.TotalUnloggedMins)}    Total time: {FormatMinutes(_summarySource.TotalTotalMins)}";

        // 5. Selection restore + the status bar's narrow-width titles
        if(_entrySource.Rows > 0)
        {
            int target = EntrySelection.RestoreIndex(visibleNewestFirst, focusId);

            // only when it actually differs, so a refresh cannot fight the user's own navigation
            if(SelectedEntryRow() != target)
            {
                _entries.Value = new TableSelection(new Point(0, target));
            }
        }

        ApplyStatusBarTitles(ViewportBudget.UseCompactStatusBarTitles(_window.Viewport.Width));

        // 6. Tell the tables their content changed. TableView measures each column from the data
        // source and caches the result (_columnsToRenderCache); mutating the source in place is
        // invisible to it, so a longer task / description / "In Progress" label stayed ellipsized
        // until something else invalidated the cache (a resize or a new Table). Update() is the
        // documented "reflect changes to Table" call that drops that cache, and the layout pass
        // below re-measures the column widths before the next paint instead of relying on a redraw.
        _summary.Update();
        _entries.Update();

        _window.SetNeedsLayout();
        _window.SetNeedsDraw();
    }

    // The status bar clips from the right when the shortcuts do not fit, which on a 60-column
    // terminal hid F6 - the only way out of the window. Titles are swapped only when the width
    // actually crosses the threshold, so the change is a no-op at normal sizes.
    private void ApplyStatusBarTitles(bool compact)
    {
        if(_shortcuts.Length == 0) return;

        string[] titles = compact ? CompactShortcutTitles : FullShortcutTitles;

        for(int i = 0; i < _shortcuts.Length; i++)
        {
            if(_shortcuts[i].Title != titles[i]) _shortcuts[i].Title = titles[i];
        }
    }
}
