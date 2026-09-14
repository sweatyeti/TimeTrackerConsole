using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui view for the migration. Phase 0 opened an empty shell; Phase 1 fills it
// with READ-ONLY parity regions: banner (top), summary table, totals line, entry list
// (fills the rest), status bar (bottom). Nothing in this path writes to disk - the
// session it renders comes from Session.LoadReadOnly, which has no EntryStore.
internal sealed class TuiSessionWindow
{
    private readonly Session? _session;

    // Phase 0 entry point (`new --tui`): an empty shell. No session exists yet, and
    // creating one would be a write, so the frame stays empty until the actions phase.
    public TuiSessionWindow()
    {
    }

    private TuiSessionWindow(Session session)
    {
        _session = session;
    }

    // Phase 1 entry point (`continue --tui`): renders the newest saved session
    // read-only. Choosing among sessions is a flow (actions phase) concern; listing and
    // selecting here would put a dialog in front of rendering that this phase is
    // verifying. With no saved sessions the empty shell is shown, as before.
    public static void RunContinue(int pageSize, ConsoleTheme theme)
    {
        List<(SessionSnapshot Snapshot, string FilePath)> sessions = EntryStore.ListAllSessions();
        if(sessions.Count == 0)
        {
            new TuiSessionWindow().Run();
            return;
        }

        Session session = Session.LoadReadOnly(sessions[0].Snapshot, pageSize, theme);
        new TuiSessionWindow(session).Run();
    }

    public void Run()
    {
        // Q4: full-screen is the default AppModel (verified) - no assignment needed
        using IApplication app = Application.Create();
        app.Init();
        using Window window = BuildWindow(app);
        app.Run(window);
    }

    private Window BuildWindow(IApplication app)
    {
        TuiPalette palette = new(_session?.Theme ?? ConsoleTheme.Resolve(null));

        Window window = new()
        {
            Title = _session is null ? "TimeTracker" : $"TimeTracker - {_session.Name}",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        window.SetScheme(palette.BaseScheme);

        // --- banner (top, height derived from the art's own line count) -------------
        bool isActive = _session?.IsActive == true;
        string art = Session.BuildActiveStateArt(isActive ? "ACTIVE" : "NOT ACTIVE");

        Label banner = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = ArtHeightInLines(art),
            Text = art
        };
        banner.SetScheme(new Scheme(isActive ? palette.BannerActive : palette.BannerInactive));
        window.Add(banner);

        // --- summary + totals + entries --------------------------------------------
        View? previous = banner;

        // the Spectre path skips the whole summary section when there are no entries
        if(_session is not null && _session.EntryCount > 0)
        {
            SummaryTableSource summarySource = new(_session.VisibleEntriesOldestFirst, palette);

            TableStyle summaryStyle = new()
            {
                ShowHeaders = true,
                ShowHorizontalHeaderOverline = false,
                ShowHorizontalHeaderUnderline = true,
                ShowHorizontalBottomLine = true,
                ShowVerticalCellLines = true,
                ExpandLastColumn = false,
                // header text keeps the theme's heading colour instead of TableView's
                // own default header scheme (part of Q8's accepted style change, but
                // the colour still comes from the theme, not from an inline colour)
                HeaderScheme = new Scheme(palette.Heading),
                RowColorGetter = summarySource.RowColorGetter
            };

            TableView summary = new()
            {
                X = 0,
                Y = Pos.Bottom(previous),
                Width = Dim.Fill(),
                Height = summaryStyle.ShowHeaders
                    ? summarySource.Rows + 3 // header row + header rule + bottom line
                    : summarySource.Rows + 1,
                Table = summarySource,
                Style = summaryStyle
            };
            summary.SetScheme(palette.BaseScheme);
            window.Add(summary);
            previous = summary;

            // totals line: same text and same "none"-group exclusion as RenderTotalsLine
            Label totals = new()
            {
                X = 0,
                Y = Pos.Bottom(previous),
                Width = Dim.Fill(),
                Height = 1,
                Text = $"Total unlogged task time: {FormatMinutes(summarySource.TotalUnloggedMins)}    Total time: {FormatMinutes(summarySource.TotalTotalMins)}"
            };
            totals.SetScheme(new Scheme(palette.Totals));
            window.Add(totals);
            previous = totals;
        }

        // --- entry list, filling everything above the status bar --------------------
        EntryListDataSource entrySource = new(_session?.VisibleEntriesNewestFirst ?? Array.Empty<TimeEntry>(), palette);
        ListView entries = new()
        {
            X = 0,
            Y = Pos.Bottom(previous),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            Source = entrySource
        };
        entries.SetScheme(palette.BaseScheme);
        if(entrySource.Count > 0)
        {
            entries.SelectedItem = 0;
        }
        window.Add(entries);

        // --- status bar (bottom) ---------------------------------------------------
        StatusBar statusBar = new(new List<Shortcut>
        {
            new(Key.Esc, "Quit", () => app.RequestStop(), "Exit the read-only view")
        })
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill()
        };
        statusBar.SetScheme(palette.BaseScheme);
        window.Add(statusBar);

        return window;
    }

    // the art is a fixed 5-row block; deriving the height from the string keeps the
    // glyph table in Session as the single source of truth
    private static int ArtHeightInLines(string art) => art.Count(character => character == '\n') + 1;

    private static string FormatMinutes(double minutes) => $"{TimeSpan.FromMinutes(minutes):hh\\:mm}";
}
