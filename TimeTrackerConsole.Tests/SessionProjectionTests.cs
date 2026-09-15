using Xunit;

// The projections both UIs render from: deleted-entry filtering, the task-group projections, and the
// two ITableSource adapters (incl. their safety on odd-but-deserializable data).
public class SessionProjectionTests
{
    private static Session SessionWith(params EntrySnapshot[] entries)
        => TestHarness.ReadOnlySession(TestHarness.Snapshot(entries.ToList()));

    [Fact]
    public void DeletedEntries_AreExcludedFromEveryVisibleProjection()
    {
        Session session = SessionWith(
            TestHarness.Entry(1, task: "weeding", isDeleted: true),
            TestHarness.Entry(2, task: "weeding", logged: false),
            TestHarness.Entry(3, task: "reading", isDeleted: true, isComplete: false));

        Assert.Equal(1, session.EntryCount);
        Assert.Equal(new[] { 2 }, session.VisibleEntriesOldestFirst.Select(entry => entry.Id).ToArray());
        Assert.Equal(new[] { 2 }, session.VisibleEntriesNewestFirst.Select(entry => entry.Id).ToArray());
        Assert.Equal(new[] { "weeding" }, session.UnloggedTaskGroups.ToArray());
        Assert.Equal(1, session.UnloggedEntryCount("weeding"));
        Assert.Equal(new[] { 1, 3 }, session.DeletedEntriesOldestFirst.Select(entry => entry.Id).ToArray());

        // a deleted entry that is also unfinished must not mark the session active
        Assert.False(session.IsActive);
    }

    [Fact]
    public void UnloggedTaskGroups_ExcludeInProgressNoneAndDeletedWork()
    {
        Session session = SessionWith(
            TestHarness.Entry(1, task: "weeding", logged: false),
            TestHarness.Entry(2, task: "Weeding", logged: false),
            TestHarness.Entry(3, task: "weeding", logged: true),
            TestHarness.Entry(4, task: "weeding", logged: false, isComplete: false),
            TestHarness.Entry(5, task: "none", logged: false),
            TestHarness.Entry(6, task: "reading", logged: false, isDeleted: true));

        Assert.Equal(new[] { "weeding" }, session.UnloggedTaskGroups.ToArray());
        Assert.Equal(2, session.UnloggedEntryCount("weeding"));
        Assert.Equal(2, session.UnloggedEntryCount("WEEDING"));   // case-insensitive, logged one excluded
    }

    [Fact]
    public void VisibleEntriesNewestFirst_IsDescendingById()
    {
        Session session = SessionWith(
            TestHarness.Entry(1), TestHarness.Entry(5), TestHarness.Entry(3));

        Assert.Equal(new[] { 5, 3, 1 }, session.VisibleEntriesNewestFirst.Select(entry => entry.Id).ToArray());
        Assert.Equal(new[] { 1, 3, 5 }, session.VisibleEntriesOldestFirst.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void FindEntry_ProjectsInProgressState()
    {
        Session session = SessionWith(TestHarness.Entry(1, isComplete: false));

        Assert.True(session.IsActive);
        Assert.NotNull(session.FindEntry(1));
        Assert.Null(session.FindEntry(2));
    }

    // ---- ITableSource adapters ---------------------------------------------------------------

    private static TuiSchemes Schemes() => new(ConsoleTheme.Resolve(null));

    [Fact]
    public void SummaryTableSource_GroupsTasksAndExcludesNoneTotals()
    {
        Session session = SessionWith(
            TestHarness.Entry(1, task: "weeding", logged: false, minutes: 30),
            TestHarness.Entry(2, task: "weeding", logged: true, minutes: 30),
            TestHarness.Entry(3, task: "none", logged: false, minutes: 15),
            TestHarness.Entry(4, task: "reading", logged: false, isComplete: false));

        SummaryTableSource source = new(session.VisibleEntriesOldestFirst, Schemes());

        Assert.Equal(2, source.Rows);
        Assert.Equal("weeding", source[0, 0]);
        Assert.Equal("none", source[1, 0]);
        Assert.Equal("2", source[0, 1]);

        // totals accumulate only across named tasks (issue #15) - the "none" group is excluded
        Assert.Equal(30, source.TotalUnloggedMins);
        Assert.Equal(60, source.TotalTotalMins);
    }

    [Fact]
    public void SummaryTableSource_SkipsDeletedEntriesAndSurvivesANullTask()
    {
        Session session = SessionWith(
            TestHarness.Entry(1, task: "weeding", isDeleted: true),
            TestHarness.Entry(2, task: "reading"));

        SummaryTableSource source = new(session.VisibleEntriesOldestFirst, Schemes());

        Assert.Equal(1, source.Rows);
        Assert.Equal("reading", source[0, 0]);

        // a null task can only arrive from a non-normalized path; the projection must not throw
        TimeEntry rogue = TimeEntry.FromSnapshot(9, new DateTime(2026, 9, 14, 9, 0, 0), new DateTime(2026, 9, 14, 9, 30, 0), null!, null!, false, true, false);
        SummaryTableSource rogueSource = new(new[] { rogue }, Schemes());

        Assert.Equal(1, rogueSource.Rows);
        Assert.Equal(string.Empty, rogueSource[0, 0]);
    }

    [Fact]
    public void EntryTableSource_RendersTheConsoleColumnsAndFiltersDeleted()
    {
        Session session = SessionWith(
            TestHarness.Entry(1, task: "weeding", description: "back bed", logged: true),
            TestHarness.Entry(2, task: "reading", description: null, isComplete: false),
            TestHarness.Entry(3, task: "gone", isDeleted: true));

        EntryTableSource source = new(session.VisibleEntriesNewestFirst);

        Assert.Equal(2, source.Rows);
        Assert.Equal("#2", source[0, 0]);
        Assert.Equal("reading", source[0, 1]);
        Assert.Equal("In Progress", source[0, 2].ToString()!.Split(" - ")[1]);
        Assert.Equal("N/A", source[0, 3]);              // in progress has no logged state
        Assert.Equal("No description", source[0, 4]);

        Assert.Equal("Logged", source[1, 3]);
        Assert.Equal("back bed", source[1, 4]);

        // out-of-range reads are empty, not exceptions
        Assert.Equal(string.Empty, source[99, 0]);
        Assert.Null(source.EntryAt(99));
    }

    [Fact]
    public void EntryTableSource_HasLoggedStateIsNullSafe()
    {
        TimeEntry rogue = TimeEntry.FromSnapshot(9, DateTime.Now, null, null!, null!, false, true, false);

        Assert.False(EntryTableSource.HasLoggedState(rogue));
        Assert.Equal("N/A", new EntryTableSource(new[] { rogue })[0, 3]);
    }
}
