using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Phase 1 (T1.1): the summary table's data source. Reproduces the task-group
// projection of Session.DisplaySummary() exactly - same grouping key (lowercased
// task), same counting, same ceiling-of-minutes math, same "none" exclusion from the
// totals line - so both UIs report the same numbers for the same session file.
//
// The Spectre renderer stays untouched: this is a second reader of the same state,
// not a replacement.
internal sealed class SummaryTableSource : ITableSource
{
    private static readonly string[] HeaderNames = { "Task", "Count", "Unlogged (hh:mm)", "Total (hh:mm)" };

    private readonly List<SummaryRow> _rows = new();
    private readonly Scheme _plainScheme;
    private readonly Scheme _unloggedScheme;

    private readonly record struct SummaryRow(string Task, int Count, double UnloggedMins, double TotalMins, bool HighlightUnlogged);

    public SummaryTableSource(IEnumerable<TimeEntry> entries, TuiPalette palette)
    {
        _plainScheme = palette.BaseScheme;
        _unloggedScheme = palette.UnloggedScheme();

        var taskQuery =
            from entry in entries
            where entry.IsComplete == true && !entry.IsDeleted
            group entry by entry.Task.ToLower() into taskGroup
            select new
            {
                Task = taskGroup.Key,
                EntryCount = taskGroup.Count(),
                TotalMins = taskGroup.Sum(s => Math.Ceiling((s.EndTime - s.StartTime).TotalMinutes)),
                UnloggedMins = taskGroup.Sum(s => Math.Ceiling(s.Logged ? 0 : (s.EndTime - s.StartTime).TotalMinutes))
            };

        foreach(var taskGroup in taskQuery)
        {
            // totals accumulate only across non-empty-task groups (issue #15), and the
            // same rule decides the red emphasis: the "none" pseudo-task stays plain
            bool emptyTask = string.IsNullOrEmpty(taskGroup.Task) || taskGroup.Task.Equals("none", StringComparison.OrdinalIgnoreCase);
            if(!emptyTask)
            {
                TotalUnloggedMins += taskGroup.UnloggedMins;
                TotalTotalMins += taskGroup.TotalMins;
            }

            _rows.Add(new SummaryRow(
                taskGroup.Task,
                taskGroup.EntryCount,
                taskGroup.UnloggedMins,
                taskGroup.TotalMins,
                HighlightUnlogged: !emptyTask && taskGroup.UnloggedMins > 0));
        }
    }

    public double TotalUnloggedMins { get; }

    public double TotalTotalMins { get; }

    public string[] ColumnNames => HeaderNames;

    public int Columns => HeaderNames.Length;

    public int Rows => _rows.Count;

    public object this[int row, int col]
    {
        get
        {
            SummaryRow cell = _rows[row];
            return col switch
            {
                0 => cell.Task,
                1 => cell.Count.ToString(),
                2 => FormatMinutes(cell.UnloggedMins),
                3 => FormatMinutes(cell.TotalMins),
                _ => string.Empty
            };
        }
    }

    // per-row emphasis, replacing the Spectre path's red unlogged cell: a named task
    // with unlogged minutes gets the theme's inactive (red) colour, everything else
    // stays on the plain scheme
    public RowColorGetterDelegate RowColorGetter =>
        args => args.RowIndex >= 0 && args.RowIndex < _rows.Count && _rows[args.RowIndex].HighlightUnlogged
            ? _unloggedScheme
            : _plainScheme;

    private static string FormatMinutes(double minutes) => $"{TimeSpan.FromMinutes(minutes):hh\\:mm}";
}
