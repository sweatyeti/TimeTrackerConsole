using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal class EntryStore
{
    private readonly object _mutationLock = new();
    private volatile bool _dirty;
    private Task? _timerTask;
    private CancellationTokenSource? _cts;

    private readonly string _filePath;
    private readonly string _sessionName;
    private readonly Guid _sessionId;
    private readonly DateTime _startedAt;
    private readonly Func<SessionSnapshot> _snapshotFunc;

    private static readonly char[] _invalidSlugChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public object MutationLock => _mutationLock;

    public EntryStore(string sessionName, Guid sessionId, DateTime startedAt, Func<SessionSnapshot> snapshotFunc)
    {
        _sessionName = sessionName;
        _sessionId = sessionId;
        _startedAt = startedAt;
        _snapshotFunc = snapshotFunc;

        string entriesDir = "entries";
        Directory.CreateDirectory(entriesDir);

        string slug = Slugify(sessionName);
        _filePath = ResolveCollision(entriesDir, slug);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _timerTask = Task.Run(() => TimerLoopAsync(_cts.Token));
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public Task FlushAsync()
    {
        // Stop the background timer
        _cts?.Cancel();
        if (_timerTask != null)
        {
            try
            {
                _timerTask.Wait(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // timer already finished or timed out; proceed with final flush
            }
        }

        // Ensure a final write regardless of dirty state
        _dirty = true;
        DoFlush();

        return Task.CompletedTask;
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_dirty)
            {
                _dirty = false;
                DoFlush();
            }
        }
    }

    private void DoFlush()
    {
        // Best-effort cleanup of orphaned .tmp from a prior crash
        CleanupOrphanTmp();

        // Take a snapshot under the mutation lock (fast in-memory copy only)
        SessionSnapshot snapshot;
        lock (_mutationLock)
        {
            snapshot = _snapshotFunc();
        }

        // Serialize (no lock held — we own the snapshot)
        string json = JsonSerializer.Serialize(snapshot, _jsonOptions);

        // Atomic write: write to .tmp, then rename over the final file
        string tmpPath = _filePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private void CleanupOrphanTmp()
    {
        try
        {
            string tmpPath = _filePath + ".tmp";
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }
        }
        catch
        {
            // best effort — ignore failures
        }
    }

    private static string Slugify(string name)
    {
        name = name.Replace(" ", "-");
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(_invalidSlugChars, c) == -1)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string ResolveCollision(string entriesDir, string slug)
    {
        string candidate = Path.Combine(entriesDir, $"{slug}.json");
        int counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(entriesDir, $"{slug}-{counter}.json");
            counter++;
        }
        return candidate;
    }
}

internal class SessionSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public List<EntrySnapshot> Entries { get; set; } = new();
}

internal class EntrySnapshot
{
    public int Id { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Task { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Logged { get; set; }
    public bool IsComplete { get; set; }
}
