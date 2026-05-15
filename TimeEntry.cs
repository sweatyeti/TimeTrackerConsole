public class TimeEntry
{
    public int Id { get; private set;}
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; } = DateTime.MinValue;
    public string Task { get; set; } = string.Empty;
    public string Description { get; set; }  = string.Empty;
    public bool Logged { get; set; } = false;
    public bool IsComplete { get; set; }
    public static int LatestAssignedID => _nextId-1;

    private static int _nextId = 1;

    private TimeEntry() { }

    public static TimeEntry GetNextEntry()
    {
        return new TimeEntry
        {
            Id = _nextId++,
            StartTime = DateTime.Now
        };
    }
}