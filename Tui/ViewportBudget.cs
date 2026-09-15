using System;

// How much vertical space each region of the window may take.
//
// Pure arithmetic over the window height so the budget is unit-testable and there is exactly ONE
// place holding the numbers - the alternative that this replaces was a summary height of
// "rows + 3" that ignored the window entirely: with enough task groups the summary pushed the entry
// table and the totals line off the bottom of the screen.
//
// The region stack, top to bottom (mirrors the Pos/Dim chain in TuiSessionWindow.BuildOnce):
//
//   rows 0..4        banner (5 art rows)
//   row  5           gap
//   rows 6..6+h-1    summary table: header + header rule + h-3 data rows + bottom line
//   row  6+h         gap
//   row  7+h         totals line
//   row  8+h         gap
//   rows 9+h..H-2    entry table: Dim.Fill(1), at least MinimumEntryRows when it has content
//   row  H-1         status bar (Pos.AnchorEnd(1))
//
// so the entry table ends up with H - 10 - h rows.
internal static class ViewportBudget
{
    internal const int BannerRows = 5;
    internal const int RegionGapRows = 3;   // above the summary, above the totals, above the entries
    internal const int TotalsRows = 1;
    internal const int StatusBarRows = 1;

    // a TableView spends 3 of its rows on the header, the header rule and the bottom line
    internal const int SummaryChromeRows = 3;
    internal const int MinimumSummaryRows = 1;

    // the entry table keeps at least a few rows on screen even when the summary is at its cap, so
    // entries stay reachable (they scroll inside the table) with many task groups
    internal const int MinimumEntryRows = 3;

    internal const int MinimumSummaryHeight = SummaryChromeRows + MinimumSummaryRows;

    // below this width the status bar's full shortcut titles no longer fit and the last shortcuts
    // (F6 = the only way out) get clipped
    internal const int CompactStatusBarWidth = 70;

    // rows the other regions need before the summary gets any
    internal static int ReservedRows(bool hasVisibleEntries) => BannerRows
                                                              + RegionGapRows
                                                              + TotalsRows
                                                              + StatusBarRows
                                                              + (hasVisibleEntries ? MinimumEntryRows : 0);

    // The height (in rows) for the summary table: its natural height (data rows + chrome) capped by
    // whatever is left once the other regions have their reservation.
    //
    // 0 means "do not show it": either there is no layout yet (windowHeight <= 0, i.e. before the
    // first layout pass), or the window is too small even for the header and one data row. The
    // caller collapses the view in that case, because an invisible view still occupies layout space.
    internal static int SummaryHeight(int windowHeight, int summaryRowCount, bool hasVisibleEntries)
    {
        if(windowHeight <= 0) return 0;

        int available = windowHeight - ReservedRows(hasVisibleEntries);

        if(available < MinimumSummaryHeight) return 0;

        int natural = Math.Max(summaryRowCount, 0) + SummaryChromeRows;

        return Math.Min(natural, available);
    }

    // 0 width means the window has not been laid out yet - keep the full titles until it has.
    internal static bool UseCompactStatusBarTitles(int windowWidth) =>
        windowWidth > 0 && windowWidth < CompactStatusBarWidth;
}
