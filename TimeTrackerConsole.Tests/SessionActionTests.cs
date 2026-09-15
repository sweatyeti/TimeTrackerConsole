using Xunit;

// The shared action surface (Session) that both UIs drive: the guards, the transitions and the
// edge cases a resumed/hand-edited session can put it in.
public class SessionActionTests
{
    [Fact]
    public void StopCurrentEntry_StopsTheUnfinishedEntry_WhenAHigherCompletedEntryExists()
    {
        // entry 2 finished, entry 1 still running - the shape a session file has after a crash. The
        // old implementation only ever looked at the highest id and did nothing here.
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: false),
            TestHarness.Entry(2, isComplete: true)
        }));

        Assert.True(session.IsActive);

        session.StopCurrentEntry();

        Assert.True(session.FindEntry(1)!.IsComplete);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void StopCurrentEntry_IgnoresDeletedEntries()
    {
        // highest id is an incomplete DELETED entry (possible in a hand-edited file): the live
        // in-progress entry below it is the one to stop
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: false),
            TestHarness.Entry(2, isComplete: false, isDeleted: true)
        }));

        Assert.True(session.IsActive);

        session.StopCurrentEntry();

        Assert.True(session.FindEntry(1)!.IsComplete);
        Assert.False(session.FindEntry(2)!.IsComplete);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void StopCurrentEntry_WithSeveralUnfinishedEntries_LeavesTheSessionActive()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: false),
            TestHarness.Entry(2, isComplete: false)
        }));

        session.StopCurrentEntry();

        Assert.True(session.FindEntry(2)!.IsComplete);   // the highest unfinished id
        Assert.False(session.FindEntry(1)!.IsComplete);
        Assert.True(session.IsActive);                   // entry 1 is still running
    }

    [Fact]
    public void StopCurrentEntry_CorrectsAStaleActiveFlag_InsteadOfRepeatingANoOp()
    {
        // nothing is in progress, but the file said the session was unfinished: IsActive is inferred
        // from the entries, so this is the "walked into it" case - it must settle, not loop
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: true),
            TestHarness.Entry(2, isComplete: true)
        }));

        Assert.False(session.IsActive);
        session.StopCurrentEntry();

        Assert.False(session.IsActive);
        Assert.True(session.FindEntry(2)!.IsComplete);
    }

    [Fact]
    public void StartNewEntryWithTask_ReturnsTheNewEntryId_AndAppliesTheNoneRule()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(4, isComplete: true)
        }));

        int blankId = session.StartNewEntryWithTask("   ");

        Assert.Equal(5, blankId);                          // reseeded after the highest existing id
        Assert.Equal("none", session.FindEntry(blankId)!.Task);
        Assert.True(session.IsActive);

        int namedId = session.StartNewEntryWithTask("  weeding  ");

        Assert.Equal(6, namedId);
        Assert.Equal("weeding", session.FindEntry(namedId)!.Task);  // trimmed, as the console prompt is
    }

    [Fact]
    public void ApplyEntryUpdate_AppliesAllThreeFields_AndOnlyTouchesLoggedWhenGiven()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "old", description: "before", logged: false)
        }));

        // logged == null means "this entry has no logged state" - the field must be left alone
        session.ApplyEntryUpdate(1, null, "new", "after");

        TimeEntry entry = session.FindEntry(1)!;
        Assert.Equal("new", entry.Task);
        Assert.Equal("after", entry.Description);
        Assert.False(entry.Logged);

        session.ApplyEntryUpdate(1, true, null, null);

        Assert.True(entry.Logged);
        Assert.Equal(string.Empty, entry.Task);       // null strings collapse to empty, not to null
        Assert.Equal(string.Empty, entry.Description);
    }

    [Fact]
    public void ApplyEntryUpdate_BumpsTheUnloggedProjection()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "weeding", logged: false)
        }));

        Assert.Equal(new[] { "weeding" }, session.UnloggedTaskGroups.ToArray());
        Assert.Equal(1, session.UnloggedEntryCount("weeding"));

        session.ApplyEntryUpdate(1, true, "weeding", null);

        Assert.Empty(session.UnloggedTaskGroups);
        Assert.Equal(0, session.UnloggedEntryCount("weeding"));
        Assert.False(session.HasLoggedState(0));
    }

    [Fact]
    public void ApplyEntryDelete_OnlyDeletesCompletedLiveEntries()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: true),
            TestHarness.Entry(2, isComplete: false)
        }));

        Assert.True(session.IsDeletableEntry(1));
        Assert.False(session.IsDeletableEntry(2));     // in progress
        Assert.False(session.IsDeletableEntry(99));    // not present

        Assert.True(session.ApplyEntryDelete(1));
        Assert.False(session.ApplyEntryDelete(2));
        Assert.False(session.ApplyEntryDelete(1));     // already deleted is not deletable again

        Assert.True(session.FindEntry(1)!.IsDeleted);
        Assert.False(session.FindEntry(2)!.IsDeleted);
    }

    [Fact]
    public void ApplyEntryRestore_OnlyRestoresDeletedEntries()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isDeleted: true),
            TestHarness.Entry(2)
        }));

        Assert.Equal(new[] { 1 }, session.DeletedEntriesOldestFirst.Select(entry => entry.Id).ToArray());

        Assert.False(session.ApplyEntryRestore(2));    // not deleted
        Assert.False(session.ApplyEntryRestore(99));   // not present
        Assert.True(session.ApplyEntryRestore(1));

        Assert.False(session.FindEntry(1)!.IsDeleted);
        Assert.Empty(session.DeletedEntriesOldestFirst);
        Assert.Equal(new[] { 2, 1 }, session.VisibleEntriesNewestFirst.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void ApplyLogTaskGroup_SkipsInProgressDeletedAndOtherTasks()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "weeding", logged: false),
            TestHarness.Entry(2, task: "weeding", logged: false, isComplete: false),
            TestHarness.Entry(3, task: "weeding", logged: false, isDeleted: true),
            TestHarness.Entry(4, task: "reading", logged: false)
        }));

        Assert.True(session.ApplyLogTaskGroup("weeding"));

        Assert.True(session.FindEntry(1)!.Logged);
        Assert.False(session.FindEntry(2)!.Logged);   // in progress
        Assert.False(session.FindEntry(3)!.Logged);   // deleted
        Assert.False(session.FindEntry(4)!.Logged);   // different task

        Assert.Equal(new[] { "reading" }, session.UnloggedTaskGroups.ToArray());
    }

    [Fact]
    public void ApplyLogTaskGroup_MatchesTheTaskCaseInsensitively()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "Weeding", logged: false)
        }));

        Assert.True(session.ApplyLogTaskGroup("weeding"));
        Assert.True(session.FindEntry(1)!.Logged);
    }

    [Fact]
    public void ApplyLogTaskGroup_RejectsAnEmptyTask()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "weeding", logged: false)
        }));

        Assert.False(session.ApplyLogTaskGroup(string.Empty));
        Assert.False(session.FindEntry(1)!.Logged);
    }

    [Fact]
    public void EndSession_StopsTheEntryAndStampsTheEnd()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, isComplete: false)
        }));

        session.EndSession();

        Assert.True(session.FindEntry(1)!.IsComplete);
        Assert.False(session.IsActive);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public void HasLoggedState_RequiresACompletedRealTask()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: "weeding", isComplete: true),
            TestHarness.Entry(2, task: "none", isComplete: true),
            TestHarness.Entry(3, task: "weeding", isComplete: false)
        }));

        Assert.True(session.HasLoggedState(1));
        Assert.False(session.HasLoggedState(2));   // the "none" pseudo-task
        Assert.False(session.HasLoggedState(3));   // in progress
        Assert.False(session.HasLoggedState(99));
    }
}
