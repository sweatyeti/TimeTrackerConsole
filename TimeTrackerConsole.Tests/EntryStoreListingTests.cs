using System.Text.Json;
using Xunit;

// ListAllSessions: what the continue screens are allowed to offer, and what they must report instead.
// Each test here runs in its own entries/ directory (see TestHarness.IsolatedEntries).
public class EntryStoreListingTests
{
    private static string WriteFile(string entriesDirectory, string name, string content)
    {
        string path = TestHarness.SessionPathIn(entriesDirectory, name);
        File.WriteAllText(path, content);

        return path;
    }

    private static string ValidSessionJson(string name, string startedAt = "2026-09-14T09:00:00") => $$"""
    {
      "schemaVersion": 2,
      "sessionId": "22222222-2222-2222-2222-222222222222",
      "name": "{{name}}",
      "startedAt": "{{startedAt}}",
      "endedAt": null,
      "entries": [
        { "id": 1, "startTime": "2026-09-14T09:00:00", "endTime": "2026-09-14T09:30:00", "task": "weeding", "description": "", "logged": false, "isComplete": true, "isDeleted": false }
      ]
    }
    """;

    [Fact]
    public void Listing_ReportsUnreadableAndUnsupportedFiles_WithoutTouchingThem()
    {
        using IDisposable scope = TestHarness.IsolatedEntries(out string entries);

        string readable = WriteFile(entries, "good", ValidSessionJson("good"));
        string brokenJson = WriteFile(entries, "broken", "{ this is not json");
        string futureSchema = WriteFile(entries, "future", ValidSessionJson("future").Replace("\"schemaVersion\": 2", "\"schemaVersion\": 99"));

        // a non-GUID sessionId is a JSON-level failure the real loader has to survive
        string badGuid = WriteFile(entries, "badguid", ValidSessionJson("badguid").Replace("22222222-2222-2222-2222-222222222222", "not-a-guid"));

        SessionFileListing listing = EntryStore.ListAllSessions();

        // only the readable session is offered...
        Assert.Equal(readable, Assert.Single(listing.Sessions).FilePath);

        // ...but every other file is named with a reason rather than silently missing
        Assert.Equal(3, listing.Skipped.Count);
        Assert.Single(listing.Skipped, skipped => skipped.FilePath == brokenJson);
        Assert.Single(listing.Skipped, skipped => skipped.FilePath == futureSchema && skipped.Reason.Contains("newer", StringComparison.OrdinalIgnoreCase));
        Assert.Single(listing.Skipped, skipped => skipped.FilePath == badGuid);

        // listing never repairs or removes a file it cannot offer
        Assert.True(File.Exists(brokenJson));
        Assert.True(File.Exists(futureSchema));
        Assert.True(File.Exists(badGuid));
    }

    [Fact]
    public void Listing_NormalizesWhatItOffers()
    {
        using IDisposable scope = TestHarness.IsolatedEntries(out string entries);

        // a file that deserializes but has a null task: offered (it is renderable after repair), and
        // the snapshot the caller receives is already normalized
        WriteFile(entries, "nulltask", """
        {
          "schemaVersion": 2,
          "sessionId": "33333333-3333-3333-3333-333333333333",
          "name": null,
          "startedAt": "2026-09-14T10:00:00",
          "endedAt": null,
          "entries": [ { "id": 1, "startTime": "2026-09-14T10:00:00", "endTime": null, "task": null, "description": null, "logged": false, "isComplete": false, "isDeleted": false } ]
        }
        """);

        SessionFileListing listing = EntryStore.ListAllSessions();

        (SessionSnapshot snapshot, _) = Assert.Single(listing.Sessions);
        Assert.Equal(SnapshotNormalizer.UnnamedSessionName, snapshot.Name);
        Assert.Equal("none", Assert.Single(snapshot.Entries).Task);
        Assert.Equal(string.Empty, Assert.Single(snapshot.Entries).Description);
    }

    [Fact]
    public void Listing_OrdersNewestFirst()
    {
        using IDisposable scope = TestHarness.IsolatedEntries(out string entries);

        WriteFile(entries, "old", ValidSessionJson("old", "2026-09-01T09:00:00"));
        WriteFile(entries, "newest", ValidSessionJson("newest", "2026-09-14T09:00:00"));
        WriteFile(entries, "middle", ValidSessionJson("middle", "2026-09-07T09:00:00"));

        SessionFileListing listing = EntryStore.ListAllSessions();

        Assert.Equal(
            new[] { "newest", "middle", "old" },
            listing.Sessions.Select(session => session.Snapshot.Name).ToArray());
    }

    [Fact]
    public void Listing_WithNoEntriesDirectory_IsEmptyAndSilent()
    {
        string previous = Directory.GetCurrentDirectory();
        string empty = Path.Combine(Path.GetTempPath(), "ttc-tests-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        Directory.SetCurrentDirectory(empty);

        try
        {
            SessionFileListing listing = EntryStore.ListAllSessions();

            Assert.Empty(listing.Sessions);
            Assert.Empty(listing.Skipped);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void TryLoadSnapshot_ReportsTheReasonForABadFile()
    {
        string path = TestHarness.ScratchSessionPath("bad-json-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "not json at all");

        Assert.False(EntryStore.TryLoadSnapshot(path, out SessionSnapshot? snapshot, out string error));
        Assert.Null(snapshot);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryLoadSnapshot_RoundTripsARealFile()
    {
        string path = TestHarness.ScratchSessionPath("roundtrip-" + Guid.NewGuid().ToString("N"));

        // serialized with the app's own options, then read back through the app's own loader
        SessionSnapshot original = TestHarness.Snapshot(new List<EntrySnapshot> { TestHarness.Entry(1, task: "reading") });
        File.WriteAllText(path, JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        Assert.True(EntryStore.TryLoadSnapshot(path, out SessionSnapshot? loaded, out _));
        Assert.NotNull(loaded);
        Assert.Equal(original.SessionId, loaded!.SessionId);
        Assert.Equal("reading", Assert.Single(loaded.Entries).Task);
    }
}
