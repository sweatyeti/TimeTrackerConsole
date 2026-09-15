using Terminal.Gui.Text;
using Xunit;

// What the entry/summary tables' column widths rest on, pinned without a driver.
//
// The ticket behind this file claimed the entry table's columns misalign for wide (CJK/emoji) text
// because the widths come from *character* counts. They do not: terminal.gui 2.4.17 measures a cell
// with `string.GetColumns()` (Wcwidth per rune, summed per grapheme cluster and clamped to two
// columns) and pads with the same measure, so a wide value occupies the two columns it renders in.
// What *was* character-counted is the pre-TableView ListView implementation (task/description padded
// with `string.Length` + `PadRight`), which is why the alignment assertion in tools/tui-smoke.sh now
// compares separator positions in display columns.
//
// If a terminal.Gui upgrade changes this measure, the TUI's columns and the terminal's columns would
// disagree again - these tests are where that shows up first.
public class WideTextMeasurementTests
{
    private static TimeEntry Entry(int id, string task, string description, bool isComplete = true)
        => TimeEntry.FromSnapshot(
            id,
            new DateTime(2026, 9, 14, 9, 0, 0),
            isComplete ? new DateTime(2026, 9, 14, 9, 30, 0) : null,
            task,
            description,
            false,
            isComplete,
            false);

    // ---- the measurement itself ------------------------------------------------------------------
    // Expected values are the terminal's own: measured as the pane cursor column after printing the
    // string in tmux 3.6 (TERM=xterm-256color, en_US.UTF-8), which is what the reader sees.

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("\u8a08", 2)]                                  // 計
    [InlineData("\u30bf\u30b9\u30af", 6)]                      // タスク
    [InlineData("\u8a08\u753b\u30ec\u30d3\u30e5\u30fc", 12)]   // 計画レビュー: the fixture's CJK task
    [InlineData("\u00e9", 1)]                                  // é precomposed
    [InlineData("e\u0301", 1)]                                 // e + combining acute
    [InlineData("\U0001f680", 2)]                              // 🚀
    [InlineData("\U0001f468\u200d\U0001f469\u200d\U0001f467", 2)]  // ZWJ family: one cluster
    [InlineData("\u2192", 1)]                                  // → (East Asian Ambiguous, one cell)
    public void GetColumns_MeasuresDisplayColumnsNotCharacters(string text, int columns)
        => Assert.Equal(columns, text.GetColumns());

    [Fact]
    public void WideText_IsWiderThanItsCharacterCount()
    {
        // the property that made the old `.Length` column widths wrong by five columns here
        string task = "\u8a08\u753b\u30ec\u30d3\u30e5\u30fc";

        Assert.Equal(6, task.Length);
        Assert.Equal(12, task.GetColumns());
    }

    // ---- what the table sources hand to the measurement -------------------------------------------

    [Fact]
    public void EntryTableSource_ReturnsTheTaskValueUnpaddedAndUnclipped()
    {
        string task = "\u8a08\u753b\u30ec\u30d3\u30e5\u30fc";
        EntryTableSource source = new(new List<TimeEntry> { Entry(1, task, "caf\u00e9 e\u0301 \U0001f680 done") });

        // raw value: the table measures it and pads it, in display columns
        Assert.Equal(task, source.CellValueAt(0, 1));
        Assert.Equal("caf\u00e9 e\u0301 \U0001f680 done", source.CellValueAt(0, 4));
        Assert.Equal(task.GetColumns(), source.CellValueAt(0, 1).GetColumns());
    }

    [Fact]
    public void EntryTableSource_KeepsNoCharacterCountedWidthOfItsOwn()
    {
        // two rows, one wide and one ASCII: the source must not pad either one to the other's
        // *character* count - that is the bug this file pins (and the old row builder did)
        EntryTableSource source = new(new List<TimeEntry>
        {
            Entry(2, "\u30bf\u30b9\u30af", "wide"),
            Entry(1, "wide-chars", "plain ascii control")
        });

        Assert.Equal("\u30bf\u30b9\u30af", source.CellValueAt(0, 1));
        Assert.Equal("wide-chars", source.CellValueAt(1, 1));
    }

    [Fact]
    public void SummaryTableSource_ReturnsTheTaskGroupValueUnpadded()
    {
        List<TimeEntry> entries = new() { Entry(1, "\u8a08\u753b", "cjk task group") };
        SummaryTableSource source = new(entries, new TuiSchemes(ConsoleTheme.Resolve(null)));

        Assert.Equal("\u8a08\u753b", source[0, 0]);
        Assert.Equal(4, ((string)source[0, 0]!).GetColumns());
    }
}
