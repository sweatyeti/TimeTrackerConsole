using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// owns the on-disk side of the session: a background loop that wakes every 5 seconds,
// checks the dirty flag, and - if anything changed - snapshots the live in-memory
// state, serializes it, and writes it atomically (tmp file + rename) to
// entries/<slug>.json (one file per session). the UI thread never touches the disk:
// it only takes the mutation lock briefly when mutating entries and raises the dirty
// flag via MarkDirty(). serialization happens on the background thread using a
// copy-then-serialize guard: the entry data is copied to detached snapshot records
// under the mutation lock, and only that (fast) copy holds the lock - never disk I/O.
internal sealed class EntryStore
{
    public const int SchemaVersion = 1;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // default System.Text.Json DateTime handling round-trips DateTime.Now (Kind.Local)
    // as ISO 8601 with offset; camelCase property names match the file schema
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // path/device-hostile characters stripped from the filename slug (the current
    // platform's invalid set plus the Windows set, so files stay portable)
    private static readonly HashSet<char> HostileChars = BuildHostileChars();

    private readonly Session _session;
    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Task? _flushLoopTask;
    private volatile bool _dirty;

    // shared with the Session's mutation sites: held only for the fast in-memory
    // copy/assignment, never while doing disk I/O. typed as System.Threading.Lock
    // (not object) so the C# 13 lock keyword at the call sites compiles to
    // EnterScope()/Dispose instead of Monitor.Enter/Exit
    public Lock MutationLock { get; } = new();

    public EntryStore(Session session, string sessionName)
    {
        _session = session;

        // files live in entries/ relative to the current working directory
        _directoryPath = Path.Combine(Directory.GetCurrentDirectory(), "entries");
        Directory.CreateDirectory(_directoryPath);

        string slug = Slugify(sessionName);
        _filePath = ResolveCollisionFreePath(slug);
        _tmpPath = _filePath + ".tmp";
    }

    // called by the Session at every mutation site - just raises the flag the
    // background loop checks every 5 seconds
    public void MarkDirty()
    {
        _dirty = true;
    }

    // starts the background flush loop (first check is immediate, then every 5 seconds)
    public void Start()
    {
        _flushLoopTask = Task.Run(RunFlushLoopAsync);
    }

    // final flush on graceful exit: stop the periodic loop (letting any in-flight
    // flush finish), then force one last write of the current state
    public async Task FlushAsync()
    {
        _cts.Cancel();

        if(_flushLoopTask is not null)
        {
            try
            {
                await _flushLoopTask;
            }
            catch(OperationCanceledException)
            {
                // the loop observed the cancellation while waiting - expected
            }
        }

        _dirty = true;
        await TryFlushAsync();
    }

    private async Task RunFlushLoopAsync()
    {
        bool firstPass = true;
        while(!_cts.IsCancellationRequested)
        {
            if(!firstPass)
            {
                try
                {
                    await Task.Delay(FlushInterval, _cts.Token);
                }
                catch(OperationCanceledException)
                {
                    break;
                }
            }
            firstPass = false;

            if(!_dirty) continue;
            await TryFlushAsync();
        }
    }

    private async Task TryFlushAsync()
    {
        if(!_dirty) return;

        // if a previous flush is still writing (slow disk), skip this tick - the
        // dirty flag is still set so the next tick picks the state up
        if(!_flushGate.Wait(0)) return;

        try
        {
            // copy-then-serialize: snapshot the live state under the mutation lock,
            // then serialize/write without holding it. CLEAR the dirty flag BEFORE
            // taking the snapshot: if a mutation lands between the clear and the
            // snapshot it either makes it into this snapshot or re-dirties the flag
            // for the next tick — a mutation after the snapshot is never lost.
            _dirty = false;
            SessionSnapshot snapshot = _session.TakeSnapshot();

            CleanOrphanedTmpFiles();

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            using(FileStream stream = new(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using(StreamWriter writer = new(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory());
                await writer.FlushAsync();
            }

            // atomic rename: a crash mid-write leaves a stale .tmp, never a corrupt final file
            File.Move(_tmpPath, _filePath, overwrite: true);
        }
        catch(Exception)
        {
            // best effort: re-raise the flag so the next tick retries the write
            _dirty = true;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // a crashed run can leave .tmp files behind; best-effort cleanup on flush start
    private void CleanOrphanedTmpFiles()
    {
        try
        {
            foreach(string orphan in Directory.EnumerateFiles(_directoryPath, "*.tmp"))
            {
                try
                {
                    File.Delete(orphan);
                }
                catch(Exception)
                {
                    // best effort
                }
            }
        }
        catch(Exception)
        {
            // best effort
        }
    }

    // if the slug is already taken (two sessions with the same name), append -2, -3, ...
    private string ResolveCollisionFreePath(string slug)
    {
        string candidate = Path.Combine(_directoryPath, slug + ".json");
        int suffix = 2;
        while(File.Exists(candidate))
        {
            candidate = Path.Combine(_directoryPath, slug + "-" + suffix + ".json");
            suffix++;
        }
        return candidate;
    }

    // "Session 2026-08-21 23:58:12" -> "Session-2026-08-21-235812"
    // spaces become dashes; ':' and any other path/device-hostile characters are stripped
    private static string Slugify(string name)
    {
        StringBuilder sb = new(name.Length);
        foreach(char c in name)
        {
            if(c == ' ')
            {
                sb.Append('-');
            }
            else if(!HostileChars.Contains(c))
            {
                sb.Append(c);
            }
        }

        string slug = sb.ToString().Trim();
        return string.IsNullOrEmpty(slug) ? "session" : slug;
    }

    private static HashSet<char> BuildHostileChars()
    {
        HashSet<char> chars = new(Path.GetInvalidFileNameChars());

        // Windows-invalid characters, kept portable even when running on Linux
        foreach(char c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
        {
            chars.Add(c);
        }

        // control characters
        for(int i = 0; i < 32; i++)
        {
            chars.Add((char)i);
        }

        return chars;
    }
}

// the shape of the file on disk - versioned, self-describing, and stable so the
// future import feature can read these files back and identify the session from
// the file contents rather than the filename
internal sealed record SessionSnapshot(
    int SchemaVersion,
    Guid SessionId,
    string Name,
    DateTime StartedAt,
    DateTime? EndedAt,
    List<EntrySnapshot> Entries);

// IsValid is deliberately not part of the snapshot - it is a runtime sentinel only
internal sealed record EntrySnapshot(
    int Id,
    DateTime StartTime,
    DateTime? EndTime,
    string Task,
    string Description,
    bool Logged,
    bool IsComplete);
