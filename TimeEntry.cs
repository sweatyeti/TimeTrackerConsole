public class TimeEntry
{
    public int Id { get; private set;}
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; } = DateTime.MinValue;
    public string Task { get; set; } = "none";
    public string Description { get; set; }  = string.Empty;
    public bool Logged { get; set; } = false;
    public bool IsComplete { get; set; }
    public bool IsDeleted { get; set; } = false;
    public bool IsValid { get; set; } = true;
    public static int LatestAssignedID => _nextId-1;

    public static TimeEntry GetEmpty()
    {
        TimeEntry entry = new()
        {
            Id = -100,
            IsValid = false
        };
        return entry;
    }

    private static int _nextId = 1; // this should always be 1

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
