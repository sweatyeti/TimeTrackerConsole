using Xunit;

// The pure logic the TUI refresh/layout depends on, tested without a driver.
public class TuiHelperTests
{
    private static TimeEntry Entry(int id, bool isComplete = true)
        => TimeEntry.FromSnapshot(id, new DateTime(2026, 9, 14, 9, 0, 0), null, "task", "", false, isComplete, false);

    // ---- selection restore -------------------------------------------------------------------
    // The regression: a refresh replaced the entry source and then re-selected row 0, throwing the
    // user's Up/Down navigation away on every F-key action.

    [Fact]
    public void RestoreIndex_ReturnsTheRowOfTheSameEntry()
    {
        List<TimeEntry> visible = new() { Entry(3), Entry(2), Entry(1) };

        Assert.Equal(0, EntrySelection.RestoreIndex(visible, 3));
        Assert.Equal(1, EntrySelection.RestoreIndex(visible, 2));
        Assert.Equal(2, EntrySelection.RestoreIndex(visible, 1));
    }

    [Fact]
    public void RestoreIndex_WithNothingSelected_FallsBackToTheFirstRow()
    {
        Assert.Equal(0, EntrySelection.RestoreIndex(new List<TimeEntry> { Entry(3) }, null));
    }

    [Fact]
    public void RestoreIndex_WhenTheEntryIsGone_FallsBackToTheFirstRow()
    {
        // e.g. the selected entry was just deleted: the documented default is the newest entry
        Assert.Equal(0, EntrySelection.RestoreIndex(new List<TimeEntry> { Entry(3), Entry(1) }, 2));
    }

    [Fact]
    public void IndexOf_ReportsMinusOneForAMissingEntry()
    {
        Assert.Equal(-1, EntrySelection.IndexOf(new List<TimeEntry> { Entry(3) }, 99));
        Assert.Equal(-1, EntrySelection.IndexOf(new List<TimeEntry>(), 1));
    }

    // ---- viewport budget ---------------------------------------------------------------------
    // The regression: the summary table was given "rows + 3" rows regardless of the window, so enough
    // task groups pushed the totals line and the whole entry table off the screen.

    // The rows the entry table actually gets once the summary has taken `summaryHeight`: the banner,
    // the three gaps, the totals line and the status bar are the other ten rows.
    private static int EntryRows(int windowHeight, int summaryHeight) =>
        windowHeight - ViewportBudget.ReservedRows(hasVisibleEntries: false) - summaryHeight;

    [Fact]
    public void SummaryHeight_CapsTheSummarySoEntriesKeepTheirRows()
    {
        // 120x40 terminal with 40 task groups: 40 rows of data want 43 rows of table
        int height = ViewportBudget.SummaryHeight(windowHeight: 40, summaryRowCount: 40, hasVisibleEntries: true);

        Assert.True(height < 43 + ViewportBudget.MinimumEntryRows);
        Assert.True(height >= ViewportBudget.MinimumSummaryHeight);

        // whatever is left goes to the entry table, which must still hold MinimumEntryRows
        Assert.True(EntryRows(40, height) >= ViewportBudget.MinimumEntryRows,
            $"entry table got {EntryRows(40, height)} rows with a {height}-row summary");
    }

    [Fact]
    public void SummaryHeight_AtNormalSize_UsesTheNaturalHeight()
    {
        // 120x40 with two task groups: nothing needs capping
        Assert.Equal(2 + ViewportBudget.SummaryChromeRows, ViewportBudget.SummaryHeight(40, 2, hasVisibleEntries: true));
    }

    [Fact]
    public void SummaryHeight_KeepsAtLeastThreeEntryRowsAt120x40Through40Groups()
    {
        for(int groups = 1; groups <= 40; groups++)
        {
            int height = ViewportBudget.SummaryHeight(40, groups, hasVisibleEntries: true);

            Assert.True(EntryRows(40, height) >= ViewportBudget.MinimumEntryRows,
                $"{groups} groups -> summary {height}, entries {EntryRows(40, height)}");
        }
    }

    [Fact]
    public void SummaryHeight_SmallButUsableWindow_StillLeavesEntryRows()
    {
        // 60x24 with 20 groups
        int height = ViewportBudget.SummaryHeight(windowHeight: 24, summaryRowCount: 20, hasVisibleEntries: true);

        Assert.True(height >= ViewportBudget.MinimumSummaryHeight);
        Assert.True(height < 20 + ViewportBudget.SummaryChromeRows);
        Assert.True(EntryRows(24, height) >= ViewportBudget.MinimumEntryRows,
            $"entry table got {EntryRows(24, height)} rows with a {height}-row summary");
    }

    [Fact]
    public void SummaryHeight_TinyOrUnlaidOutWindow_HidesTheSummary()
    {
        Assert.Equal(0, ViewportBudget.SummaryHeight(windowHeight: 0, summaryRowCount: 5, hasVisibleEntries: true));
        Assert.Equal(0, ViewportBudget.SummaryHeight(windowHeight: 12, summaryRowCount: 5, hasVisibleEntries: true));
    }

    [Fact]
    public void SummaryHeight_WithNoVisibleEntries_ReservesNoEntryRows()
    {
        // 16 rows: with entries present there is no room for the summary chrome, so it collapses;
        // with none, the three entry rows are not reserved and the summary shows
        Assert.Equal(0, ViewportBudget.SummaryHeight(windowHeight: 16, summaryRowCount: 10, hasVisibleEntries: true));
        Assert.True(ViewportBudget.SummaryHeight(windowHeight: 16, summaryRowCount: 10, hasVisibleEntries: false)
                    >= ViewportBudget.MinimumSummaryHeight);
    }

    [Fact]
    public void CompactStatusBarTitles_OnlyOnNarrowTerminals()
    {
        Assert.False(ViewportBudget.UseCompactStatusBarTitles(120));
        Assert.False(ViewportBudget.UseCompactStatusBarTitles(ViewportBudget.CompactStatusBarWidth));
        Assert.True(ViewportBudget.UseCompactStatusBarTitles(60));

        // before the first layout the width is 0 - keep the full titles rather than guessing
        Assert.False(ViewportBudget.UseCompactStatusBarTitles(0));
    }

    // ---- active-state banner -----------------------------------------------------------------

    [Fact]
    public void BuildActiveStateArt_UsesTheRequestedSpacingWhenItFits()
    {
        string wide = Session.BuildActiveStateArt("ACTIVE", letterGap: 3, wordGap: 5, maxWidth: 120);
        string spectre = Session.BuildActiveStateArt("ACTIVE");

        Assert.Equal(5, wide.Split(Environment.NewLine).Length);
        Assert.True(wide.Split(Environment.NewLine).Max(line => line.Length) > spectre.Split(Environment.NewLine).Max(line => line.Length));
    }

    [Fact]
    public void BuildActiveStateArt_ShrinksToFitANarrowTerminal_InsteadOfBeingClipped()
    {
        // "NOT ACTIVE" at the wide TUI spacing is 71 columns; a 60-column terminal must get the
        // narrower block rather than 11 clipped columns
        string art = Session.BuildActiveStateArt("NOT ACTIVE", letterGap: 3, wordGap: 5, maxWidth: 60);

        Assert.Equal(5, art.Split(Environment.NewLine).Length);
        Assert.All(art.Split(Environment.NewLine), line => Assert.True(line.Length <= 60, $"line '{line}' is wider than 60"));
        Assert.NotEqual("NOT ACTIVE", art); // still block art, just tighter
    }

    [Fact]
    public void BuildActiveStateArt_FallsBackToThePlainLabelWhenEvenTheTightestBlockDoesNotFit()
    {
        string art = Session.BuildActiveStateArt("NOT ACTIVE", letterGap: 3, wordGap: 5, maxWidth: 30);

        Assert.Equal("NOT ACTIVE", art);
    }

    [Fact]
    public void BuildActiveStateArt_WithoutACap_IsUnchanged()
    {
        // the Spectre path calls this with defaults and no cap - it must stay byte-for-byte the same
        Assert.Equal(Session.BuildActiveStateArt("ACTIVE"), Session.BuildActiveStateArt("ACTIVE", letterGap: 1, wordGap: 3, maxWidth: 0));
    }
}
