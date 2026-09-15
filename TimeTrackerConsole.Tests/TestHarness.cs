using System.Runtime.CompilerServices;

// Runs the whole test assembly from a disposable scratch directory.
//
// EntryStore resolves entries/ relative to the process working directory and writes asynchronously,
// so a test host that stayed in the repo would create entries/*.json next to the application's own
// sessions. ModuleInitializer runs before the first test, so every Session.Resume in this assembly
// is sandboxed and no fixture or session file ever lands in the repo checkout.
internal static class TestHarness
{
    internal static string ScratchDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        ScratchDirectory = Path.Combine(Path.GetTempPath(), "ttc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ScratchDirectory, "entries"));
        Directory.SetCurrentDirectory(ScratchDirectory);
    }

    // a session file path inside the scratch directory - never the repo's own entries/
    internal static string ScratchSessionPath(string name) =>
        Path.Combine(ScratchDirectory, "entries", name + ".json");

    internal static string SessionPathIn(string entriesDirectory, string name) =>
        Path.Combine(entriesDirectory, name + ".json");

    // Some tests need an entries/ directory of their own: they chdir into a fresh temp directory for
    // the duration, so "which files does listing see?" has one deterministic answer.
    // Parallelization is disabled assembly-wide precisely so chdir is safe (see AssemblyInfo.cs).
    internal static IDisposable IsolatedEntries(out string entriesDirectory)
    {
        string previous = Directory.GetCurrentDirectory();
        string root = Path.Combine(Path.GetTempPath(), "ttc-tests-dir-" + Guid.NewGuid().ToString("N"));
        entriesDirectory = Path.Combine(root, "entries");
        Directory.CreateDirectory(entriesDirectory);
        Directory.SetCurrentDirectory(root);

        return new DirectoryScope(previous);
    }

    private sealed class DirectoryScope(string previous) : IDisposable
    {
        public void Dispose() => Directory.SetCurrentDirectory(previous);
    }

    internal static Guid SessionId => Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal static EntrySnapshot Entry(
        int id,
        string? task = "task",
        string? description = "described",
        bool isComplete = true,
        bool logged = false,
        bool isDeleted = false,
        int startHour = 9,
        int minutes = 30)
    {
        DateTime start = new(2026, 9, 14, startHour, 0, 0, DateTimeKind.Local);

        return new EntrySnapshot(
            id,
            start,
            isComplete ? start.AddMinutes(minutes) : null,
            task!,
            description!,
            logged,
            isComplete,
            isDeleted);
    }

    internal static SessionSnapshot Snapshot(
        List<EntrySnapshot>? entries,
        int schemaVersion = EntryStore.SchemaVersion,
        string? name = "test session")
        => new(
            schemaVersion,
            SessionId,
            name!,
            new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Local),
            null,
            entries!);

    // A writable session (needed for the action surface) whose background flush loop is stopped
    // again immediately: the file lives in the scratch directory, and after Shutdown the session
    // performs no further disk writes.
    internal static Session ResumedSession(SessionSnapshot snapshot)
    {
        Session session = Session.Resume(
            snapshot, ScratchSessionPath(Guid.NewGuid().ToString("N")), 30);
        session.Shutdown();

        return session;
    }

    // A session with no store at all - the projections that must never write use this
    internal static Session ReadOnlySession(SessionSnapshot snapshot) => Session.LoadReadOnly(snapshot, 30);
}
