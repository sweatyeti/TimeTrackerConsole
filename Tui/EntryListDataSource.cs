using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Phase 1 (T1.4): the Terminal.Gui entry list. Ports the padding math of
// Session.BuildEntryRow() exactly (id/task/time padded to widths derived from the live
// entry set, status column always emitted and padded to Session.StatusColumnWidth) so
// the columns land on the same offsets as the Spectre main menu.
//
// Difference from the Spectre path, by necessity: Spectre embeds markup tags after the
// pad; Terminal.Gui paints attributes. The plain text is therefore padded FIRST and
// only then split into coloured segments, so a colour can never consume a pad column.
internal sealed class EntryListDataSource : IListDataSource
{
    private readonly List<EntryRow> _rows = new();
    private readonly TuiSchemes _schemes;

    public EntryListDataSource(IEnumerable<TimeEntry> entriesNewestFirst, TuiSchemes schemes)
    {
        _schemes = schemes;
        Update(entriesNewestFirst);
    }

    // Rebuilds the rows in place and notifies the ListView. This is the documented refresh path
    // for IListDataSource: the view is long-lived, only its contents change, and the implementor
    // is responsible for raising CollectionChanged when the underlying data changes.
    public void Update(IEnumerable<TimeEntry> entriesNewestFirst)
    {
        List<TimeEntry> entries = entriesNewestFirst.Where(entry => !entry.IsDeleted).ToList();

        // column widths: id, task and time are variable, so each is padded to the widest
        // value in the live (non-deleted) entry set - same derivation as BuildEntryRow
        int idWidth = entries.Select(e => e.Id.ToString().Length).DefaultIfEmpty(0).Max();

        int taskWidth = entries.Select(e => e.Task.Length).DefaultIfEmpty(0).Max();

        int timeWidth = entries
            .Select(e => e.IsComplete
                ? $"{e.StartTime:HH:mm} - {e.EndTime:HH:mm}".Length
                : $"{e.StartTime:HH:mm} - In Progress".Length)
            .DefaultIfEmpty(0)
            .Max();

        _rows.Clear();
        foreach(TimeEntry entry in entries)
        {
            _rows.Add(BuildRow(entry, idWidth, taskWidth, timeWidth, _schemes));
        }

        RaiseCollectionChanged();
    }

    public int Count => _rows.Count;

    public int MaxItemLength => _rows.Count == 0 ? 0 : _rows.Max(row => row.Text.Length);

    public bool SuspendCollectionChangedEvent { get; set; }

    // raised by Update so the ListView repaints; the IListDataSource contract expects the
    // implementor to notify when the underlying data changes
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void RaiseCollectionChanged()
        => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    // IListDataSource is IDisposable; the rows hold no unmanaged state, so nothing to release
    public void Dispose()
    {
    }

    public bool IsMarked(int item) => false;

    public void SetMark(int item, bool value)
    {
        // marking is not used by the entry list (ListView.ShowMarks stays false)
    }

    public bool RenderMark(ListView listView, int item, int col, bool isMarked, bool isSelected) => false;

    public IList ToList() => _rows.Select(row => (object)row.Text).ToList();

    public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX)
    {
        if(item < 0 || item >= _rows.Count || width <= 0) return;

        EntryRow entryRow = _rows[item];

        // The selected row paints on the list's Focus background, so BOTH the background and the
        // foreground come from that role. IListDataSource.Render documents that the scheme is
        // already set for the selection state before this is called, and the framework picks
        // Focus only while the list actually has focus - otherwise Active. Keeping the segment's
        // own foreground put white-on-cyan for "current" and made the selected row unreadable;
        // the Spectre path likewise lets its selection highlight mask the row's own colours.
        Attribute selectedAttribute = listView.GetAttributeForRole(
            listView.HasFocus ? Terminal.Gui.Drawing.VisualRole.Focus : Terminal.Gui.Drawing.VisualRole.Active);

        foreach(RowSegment segment in entryRow.Segments)
        {
            int from = Math.Max(segment.Start, viewportX);
            int to = Math.Min(segment.Start + segment.Length, viewportX + width);
            if(from >= to) continue;

            Attribute attribute = selected ? selectedAttribute : segment.Attribute;

            listView.SetAttribute(attribute);
            listView.Move(col + (from - viewportX), row);
            for(int i = from; i < to; i++)
            {
                listView.AddRune(entryRow.Text[i]);
            }
        }
    }

    private static EntryRow BuildRow(TimeEntry entry, int idWidth, int taskWidth, int timeWidth, TuiSchemes schemes)
    {
        RowBuilder builder = new();

        builder.Append($"#{entry.Id}".PadRight(idWidth + 1), schemes.Secondary);

        builder.Append(" | ", schemes.Secondary);
        builder.Append(entry.Task.PadRight(taskWidth), schemes.Plain);

        builder.Append(" | ", schemes.Secondary);
        string timeText = $"{entry.StartTime:HH:mm} - {(entry.IsComplete ? entry.EndTime.ToString("HH:mm") : "In Progress")}".PadRight(timeWidth);
        builder.Append(timeText, entry.IsComplete ? schemes.Plain : schemes.InProgress);

        // status column: Logged / Unlogged for completed real tasks, N/A otherwise
        // (in-progress entries and "none"-task entries have no logged/unlogged state).
        // Always emitted and padded to the fixed width so the description separator
        // lands on the same column on every row.
        bool hasStatus = entry.IsComplete && !entry.Task.Equals("none", StringComparison.OrdinalIgnoreCase);
        string statusText = hasStatus ? (entry.Logged ? "Logged" : "Unlogged") : "N/A";
        Attribute statusAttribute = hasStatus
            ? (entry.Logged ? schemes.Positive : schemes.Unlogged)
            : schemes.Muted;

        builder.Append(" | ", schemes.Secondary);
        builder.Append(statusText.PadRight(Session.StatusColumnWidth), statusAttribute);

        builder.Append(" | ", schemes.Secondary);
        builder.Append(
            string.IsNullOrEmpty(entry.Description) ? "No description" : entry.Description,
            string.IsNullOrEmpty(entry.Description) ? schemes.Muted : schemes.Plain);

        return builder.Build();
    }

    internal sealed record EntryRow(string Text, IReadOnlyList<RowSegment> Segments);

    internal readonly record struct RowSegment(int Start, int Length, Attribute Attribute);

    // pads/tracks the plain text and the coloured spans over it in one pass, so the
    // two can never disagree about the column offsets
    private sealed class RowBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly List<RowSegment> _segments = new();

        public void Append(string text, Attribute attribute)
        {
            if(text.Length == 0) return;

            _segments.Add(new RowSegment(_text.Length, text.Length, attribute));
            _text.Append(text);
        }

        public EntryRow Build() => new(_text.ToString(), _segments);
    }
}
