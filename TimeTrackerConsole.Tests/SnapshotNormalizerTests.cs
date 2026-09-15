using System.Text.Json;
using Xunit;

// Snapshot normalization / validation: the malformed-but-deserializable session files that used to
// crash both renderers, and the semantic gate the continue screens depend on.
public class SnapshotNormalizerTests
{
    [Fact]
    public void MissingEntriesKey_DeserializesToNull_AndNormalizesToEmpty()
    {
        // the shape a truncated or hand-written file has: everything the records need except entries.
        // Read through the real loader (the app's own JsonOptions), not a throwaway serializer setup.
        string path = TestHarness.ScratchSessionPath("missing-entries");
        File.WriteAllText(path, """
        {
          "schemaVersion": 2,
          "sessionId": "11111111-1111-1111-1111-111111111111",
          "name": "preview",
          "startedAt": "2026-09-14T08:00:00",
          "endedAt": null
        }
        """);

        Assert.True(EntryStore.TryLoadSnapshot(path, out SessionSnapshot? snapshot, out string error));
        Assert.Empty(error);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Entries);

        SessionSnapshot normalized = SnapshotNormalizer.Normalize(snapshot);

        Assert.NotNull(normalized.Entries);
        Assert.Empty(normalized.Entries);
    }

    [Fact]
    public void Normalize_RepairsNullTaskAndDescription()
    {
        SessionSnapshot normalized = SnapshotNormalizer.Normalize(
            TestHarness.Snapshot(new List<EntrySnapshot> { TestHarness.Entry(1, task: null, description: null) }));

        EntrySnapshot entry = Assert.Single(normalized.Entries);

        Assert.Equal("none", entry.Task);
        Assert.Equal(string.Empty, entry.Description);
    }

    [Fact]
    public void Normalize_NullName_BecomesUnnamedSession()
    {
        SessionSnapshot normalized = SnapshotNormalizer.Normalize(TestHarness.Snapshot(new(), name: null));

        Assert.Equal(SnapshotNormalizer.UnnamedSessionName, normalized.Name);
    }

    [Fact]
    public void Normalize_DropsNullEntriesInTheList()
    {
        List<EntrySnapshot> entries = new()
        {
            TestHarness.Entry(1),
            null!,
            TestHarness.Entry(2)
        };

        SessionSnapshot normalized = SnapshotNormalizer.Normalize(TestHarness.Snapshot(entries));

        Assert.Equal(new[] { 1, 2 }, normalized.Entries.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void Normalize_CollapsesDuplicateIdsDeterministically()
    {
        List<EntrySnapshot> entries = new()
        {
            TestHarness.Entry(1, task: "first"),
            TestHarness.Entry(1, task: "second"),
            TestHarness.Entry(2)
        };

        SessionSnapshot normalized = SnapshotNormalizer.Normalize(TestHarness.Snapshot(entries));

        Assert.Equal(new[] { 1, 2 }, normalized.Entries.Select(entry => entry.Id).ToArray());
        Assert.Equal("second", normalized.Entries[0].Task); // last occurrence wins
    }

    [Fact]
    public void Normalize_LeavesAValidSnapshotAlone()
    {
        SessionSnapshot original = TestHarness.Snapshot(new List<EntrySnapshot> { TestHarness.Entry(1, task: "weeding") });

        SessionSnapshot normalized = SnapshotNormalizer.Normalize(original);

        EntrySnapshot entry = Assert.Single(normalized.Entries);

        Assert.Equal(original.Name, normalized.Name);
        Assert.Equal(original.SessionId, normalized.SessionId);
        Assert.Equal("weeding", entry.Task);
        Assert.Equal(original.Entries[0].StartTime, entry.StartTime);
        Assert.Equal(original.Entries[0].Logged, entry.Logged);
        Assert.Equal(original.Entries[0].IsDeleted, entry.IsDeleted);
    }

    [Fact]
    public void TryValidate_AcceptsLegacyAndCurrentSchema()
    {
        // v1 files predate isDeleted; they are still readable (the record default is false)
        Assert.True(SnapshotNormalizer.TryValidate(TestHarness.Snapshot(new(), schemaVersion: 1), out _));
        Assert.True(SnapshotNormalizer.TryValidate(TestHarness.Snapshot(new()), out _));
    }

    [Fact]
    public void TryValidate_RefusesANewerSchemaWithAReason()
    {
        bool valid = SnapshotNormalizer.TryValidate(
            TestHarness.Snapshot(new(), schemaVersion: EntryStore.SchemaVersion + 1), out string reason);

        Assert.False(valid);
        Assert.Contains("newer", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_RefusesAnUnreadableFile()
    {
        Assert.False(SnapshotNormalizer.TryValidate(null, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Resume_WithNullEntries_DoesNotThrow()
    {
        Session session = TestHarness.ResumedSession(TestHarness.Snapshot(null));

        Assert.Empty(session.VisibleEntriesNewestFirst);
        Assert.Equal(0, session.EntryCount);
    }

    [Fact]
    public void LoadReadOnly_WithNullTaskEntry_DoesNotThrow_AndCoalescesTheTask()
    {
        Session session = TestHarness.ReadOnlySession(TestHarness.Snapshot(new List<EntrySnapshot>
        {
            TestHarness.Entry(1, task: null, description: null)
        }));

        TimeEntry entry = Assert.Single(session.VisibleEntriesNewestFirst);

        Assert.Equal("none", entry.Task);
        Assert.Equal(string.Empty, entry.Description);
    }
}
