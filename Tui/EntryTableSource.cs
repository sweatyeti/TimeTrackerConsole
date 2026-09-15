using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui.Views;

// The entry list as a real table.
//
// The ListView version faked columns: one long string padded with " | " separators, plus a
// parallel list of coloured spans tracked by hand so the two could not disagree about offsets.
// TableView is the control the docs give for this ("displays and enables infinite scrolling
// through tabular data based on an ITableSource"), it virtualises by viewport, and it colours
// individual cells through ColumnStyle.ColorGetter - so the padding maths, the span bookkeeping
// and the hand-rolled clipping all go away.
//
// Values are the same ones the old row builder produced, so the Spectre path's information is
// unchanged: id, task, time range (or "In Progress"), Logged/Unlogged/N/A, description.
internal sealed class EntryTableSource : ITableSource
{
    private static readonly string[] HeaderNames = { "#", "Task", "Time", "Status", "Description" };

    private readonly List<TimeEntry> _entries = new();

    public EntryTableSource(IEnumerable<TimeEntry> entriesNewestFirst) => Update(entriesNewestFirst);

    // mutate in place; the table is long-lived like every other region
    public void Update(IEnumerable<TimeEntry> entriesNewestFirst)
    {
        _entries.Clear();
        _entries.AddRange(entriesNewestFirst.Where(entry => !entry.IsDeleted));
    }

    public int Rows => _entries.Count;

    public int Columns => HeaderNames.Length;

    public string[] ColumnNames => HeaderNames;

    public object this[int row, int col] => CellValueAt(row, col);

    // the colour getters need the entry behind the cell, not just its formatted value
    internal TimeEntry? EntryAt(int row) => row >= 0 && row < _entries.Count ? _entries[row] : null;

    internal string CellValueAt(int row, int col)
    {
        TimeEntry? entry = EntryAt(row);
        if(entry is null) return string.Empty;

        return col switch
        {
            0 => $"#{entry.Id}",
            1 => entry.Task,
            2 => TimeText(entry),
            3 => StatusText(entry),
            4 => string.IsNullOrEmpty(entry.Description) ? "No description" : entry.Description,
            _ => string.Empty
        };
    }

    // the status column's rule, shared with the colour getter so text and colour cannot diverge
    internal static bool HasLoggedState(TimeEntry entry)
        => entry.IsComplete && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static string TimeText(TimeEntry entry)
        => $"{entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "In Progress")}";

    private static string StatusText(TimeEntry entry)
        => HasLoggedState(entry) ? (entry.Logged ? "Logged" : "Unlogged") : "N/A";
}
