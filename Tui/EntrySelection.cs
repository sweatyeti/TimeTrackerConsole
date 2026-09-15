using System.Collections.Generic;

// Which row the entry table should highlight after a refresh.
//
// Kept as a pure function so the rule is testable without a driver: two shipping bugs lived on this
// exact path (Enter went to row 0 because the selection was read from Cursor, and a refresh reset
// the user's Up/Down navigation to row 0).
internal static class EntrySelection
{
    // Index of the row holding entryId in a newest-first list, or -1 when the entry is gone
    // (deleted, or filtered out by normalization).
    internal static int IndexOf(IReadOnlyList<TimeEntry> entriesNewestFirst, int entryId)
    {
        for(int i = 0; i < entriesNewestFirst.Count; i++)
        {
            if(entriesNewestFirst[i].Id == entryId) return i;
        }

        return -1;
    }

    // The row to focus: the same entry where it is still present, otherwise the first row. Row 0 is
    // the newest entry, which is also what a freshly built menu shows - so "the entry I was on is
    // gone" degrades to the documented default instead of landing somewhere arbitrary.
    //
    // entryId is null when nothing was selected before (an empty list, or the first paint): that is
    // the same first-row answer.
    internal static int RestoreIndex(IReadOnlyList<TimeEntry> entriesNewestFirst, int? entryId)
    {
        if(entryId is not { } id) return 0;

        int index = IndexOf(entriesNewestFirst, id);

        return index >= 0 ? index : 0;
    }
}
