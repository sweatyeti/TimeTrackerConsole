#!/usr/bin/env python3
"""Display-column checks on a captured tmux frame.

Why this exists: `tmux capture-pane` hands back the *cells* of the screen as text, so the number of
characters in a captured line is not the number of terminal columns it occupies. A CJK ideograph is
one character but two columns, a combining acute accent is one character and zero columns. Measured
in characters, a table row containing wide text always looks "shifted" against an ASCII row even when
the terminal shows the columns perfectly aligned.

This tool measures in display columns - the thing the reader actually sees - so the smoke harness can
assert what the ticket asked for: every row of a table puts its column separators in the same columns.

Usage:
  tools/tui-frame.py align <frame> [--chars <string>] [--min-separators N] [--require-blocks N]
  tools/tui-frame.py width <string> [<string> ...]
  tools/tui-frame.py selftest

`align` groups *contiguous* frame rows that draw at least `--min-separators` separators into table
blocks (one block = one table; the blank window filler rows carry only the two border columns and are
ignored) and requires every row of a block to place its separators in the same display columns.
`--require-blocks N` makes a frame with fewer tables than expected fail, so the check cannot pass by
finding nothing to compare. Exit code is 0 when every block lines up, non-zero otherwise; each block
is printed with its row range and separator columns, so a failure shows exactly which columns differ.

Separators are the vertical box-drawing characters Terminal.Gui's TableView draws plus the
horizontal-rule junctions that sit on the same columns (│ ┼ ├ ┤ ┬ ┴); `--chars '|'` measures the
hand-rolled ` | ` separators of the pre-TableView entry list instead.
"""

import sys
import unicodedata

DEFAULT_SEPARATORS = "\u2502\u253c\u251c\u2524\u252c\u2534\u250c\u2510\u2514\u2518"  # │ ┼ ├ ┤ ┬ ┴ ┌ ┐ └ ┘

# Characters that move the cursor by nothing: joiners/zero-width marks and variation selectors.
ZERO_WIDTH = {0x200B, 0x200C, 0x200D, 0x2060, 0xFE0E, 0xFE0F, 0xFEFF}


def rune_width(ch):
    """Columns one rune occupies on its own (before grapheme-cluster merging)."""
    codepoint = ord(ch)
    if codepoint in ZERO_WIDTH:
        return 0
    if unicodedata.combining(ch):
        return 0
    if unicodedata.category(ch) in ("Mn", "Me", "Cf"):
        return 0
    # East Asian Wide/Fullwidth: two cells. Ambiguous ('A') stays one cell, which is what a
    # non-CJK-locale terminal does - the same rule Terminal.Gui's Wcwidth-backed measurement uses.
    if unicodedata.east_asian_width(ch) in ("W", "F"):
        return 2
    if codepoint >= 0x1F300:
        return 2
    return 1


def _is_regional(codepoint):
    return 0x1F1E6 <= codepoint <= 0x1F1FF


def clusters(text):
    """Split into the grapheme clusters a terminal renders as one glyph.

    Combines a base rune with its combining marks, keeps ZWJ sequence members (and the ZWJ itself)
    in one cluster, and pairs regional-indicator flags - the three shapes where summing per-rune
    widths would over-count. Terminal.Gui clamps a cluster to two columns and so does this.
    """
    grouped = []
    current = []
    previous = None

    for ch in text:
        joins = (
            rune_width(ch) == 0
            or previous == "\u200d"  # the rune after a joiner belongs to the same cluster
            or (_is_regional(ord(ch)) and len(current) == 1 and _is_regional(ord(current[0])))
        )
        if current and joins:
            current.append(ch)
        else:
            if current:
                grouped.append(current)
            current = [ch]
        previous = ch

    if current:
        grouped.append(current)

    return grouped


def text_width(text):
    """Display columns the text occupies, the way the terminal lays it out."""
    return sum(min(2, sum(rune_width(ch) for ch in cluster)) for cluster in clusters(text))


def columns(text):
    """[(rune, display column it starts at)] for every rune in `text`."""
    placed = []
    column = 0
    for cluster in clusters(text):
        width = min(2, sum(rune_width(ch) for ch in cluster))
        placed.append((cluster[0], column))
        for ch in cluster[1:]:
            placed.append((ch, column))  # inside the same cluster: no column of its own
        column += width
    return placed


def separator_columns(line, separators=DEFAULT_SEPARATORS):
    """Display columns of `line` occupied by a column separator character."""
    return [column for ch, column in columns(line) if ch in separators]


def table_blocks(lines, separators=DEFAULT_SEPARATORS, min_separators=3, min_rows=2):
    """Contiguous runs of rows that look like table rows, as (first_row, last_row, rows 1-based)."""
    blocks = []
    current = []
    for number, line in enumerate(lines, start=1):
        if len(separator_columns(line, separators)) >= min_separators:
            current.append((number, line))
        else:
            if len(current) >= min_rows:
                blocks.append((current[0][0], current[-1][0], current))
            current = []
    if len(current) >= min_rows:
        blocks.append((current[0][0], current[-1][0], current))
    return blocks


def align(frame_path, separators=DEFAULT_SEPARATORS, min_separators=3, require_blocks=1):
    """Every row of every table block in the frame must share one set of separator columns."""
    with open(frame_path, encoding="utf-8") as handle:
        lines = handle.read().split("\n")

    blocks = table_blocks(lines, separators, min_separators, min_rows=2)
    problems = []
    compared = []

    for first, last, rows in blocks:
        expected = separator_columns(rows[0][1], separators)
        compared.append((first, last, expected))
        for number, line in rows[1:]:
            measured = separator_columns(line, separators)
            if measured != expected:
                problems.append((number, line, measured, expected))

    if len(blocks) < require_blocks:
        problems.append((0, f"only {len(blocks)} table block(s) found, expected at least {require_blocks}", [], []))

    print(f"frame: {frame_path}")
    for first, last, expected in compared:
        print(f"  rows {first}-{last}: separators at columns {expected}")

    if problems:
        print("MISALIGNED:")
        for number, line, measured, expected in problems:
            if number == 0:
                print(f"  {line}")
                continue
            print(f"  row {number}: separators at {measured}, expected {expected}")
            print(f"    {line}")
        return False

    rows_total = sum(last - first + 1 for first, last, _ in compared)
    print(f"  {len(blocks)} table block(s), {rows_total} rows compared: every row lines up")
    return True


# ---- self-test -----------------------------------------------------------------------------------
#
# The width table below was measured on this box: for each string, the pane cursor column after
# `printf %s` in tmux 3.6 (TERM=xterm-256color, en_US.UTF-8) - i.e. what the *terminal* thinks the
# string is worth - matched against what this module computes.
WIDTH_CASES = [
    ("abc", 3),
    ("\u8a08", 2),                                    # 計
    ("\u30bf\u30b9\u30af", 6),                        # タスク
    ("\u8a08\u753b\u30ec\u30d3\u30e5\u30fc", 12),     # 計画レビュー (the fixture's CJK task)
    ("\u00e9", 1),                                    # é precomposed
    ("e\u0301", 1),                                   # e + combining acute
    ("\U0001f680", 2),                                # 🚀
    ("\U0001f468\u200d\U0001f469\u200d\U0001f467", 2),  # ZWJ family: one cluster, clamped to 2
    ("\U0001f1ef\U0001f1f5", 2),                      # regional-indicator flag: one cluster, 2
    ("\u2192", 1),                                    # → ambiguous width, one cell here
    ("\u00b1", 1),                                    # ± ambiguous width
    ("\u03b1", 1),                                    # α ambiguous width
    ("\u3042", 2),                                    # あ
    ("\uff71", 1),                                    # halfwidth katakana ｱ
    ("\uff11", 2),                                    # fullwidth digit １
]

# A miniature table whose rows place their separators identically (built with the display-width
# padding the TableView applies), and the two rows quoted in the ticket. Measured in display columns
# the ticket's rows put their separators at different columns; that is what the assertion must catch,
# and it is also why the pasted frame "looks" shifted: character counts are not display columns.
def _padded(text, width):
    return text + " " * (width - text_width(text))


def _aligned_sample():
    widths = {"id": 2, "task": 12, "status": 8, "description": 10}
    rows = [
        ("#2", "\u8a08\u753b\u30ec\u30d3\u30e5\u30fc", "N/A", "done"),
        ("#1", "\u30bf\u30b9\u30af", "Unlogged", "wide chars"),
    ]
    lines = [
        "\u2502" + row[0] + "\u2502" + _padded(row[1], widths["task"]) + "\u2502"
        + _padded(row[2], widths["status"]) + "\u2502" + _padded(row[3], widths["description"]) + "\u2502"
        for row in rows
    ]
    # the header rule the TableView draws below a captioned table: its junctions sit on the same
    # columns as the data rows' separators
    rule = "\u2502" + "\u2500" * widths["id"] + "\u253c" + "\u2500" * widths["task"] + "\u253c" \
           + "\u2500" * widths["status"] + "\u253c" + "\u2500" * widths["description"] + "\u2502"
    lines.append(rule)
    return "\n".join(lines) + "\n"


MISALIGNED_SAMPLE = (
    "\u2502\u2502#2\u2502\u8a08\u753b\u30ec\u30ec\u30d3\u30e5\u30fc\u250210:00 - In Progress\u2502N/A     \u2502caf\u00e9 \u00e9 \U0001f680 done         \u2502\n"
    "\u2502\u2502#1\u2502\u30bf\u30b9\u30af      \u250209:00 - 09:30      \u2502Unlogged\u2502wide chars above       \u2502\n"
)


def selftest(workspace):
    """Check the width model and both directions of the alignment assertion."""
    failures = []

    for text, expected in WIDTH_CASES:
        measured = text_width(text)
        if measured != expected:
            failures.append(f"width of {text!r}: measured {measured}, expected {expected}")

    aligned = workspace / "selftest-aligned.txt"
    aligned.write_text(_aligned_sample(), encoding="utf-8")
    if not align(str(aligned), require_blocks=1):
        failures.append("the aligned sample was reported misaligned")

    misaligned = workspace / "selftest-misaligned.txt"
    misaligned.write_text(MISALIGNED_SAMPLE, encoding="utf-8")
    if align(str(misaligned), require_blocks=1):
        failures.append("the misaligned sample (the ticket's two quoted rows) was reported aligned")

    empty = workspace / "selftest-empty.txt"
    empty.write_text("\u2502" + " " * 20 + "\u2502\n", encoding="utf-8")
    if align(str(empty), require_blocks=1):
        failures.append("a frame with no table block was reported aligned")

    if failures:
        print("selftest FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return False

    print(f"selftest: {len(WIDTH_CASES)} width cases, aligned sample passes, "
          f"misaligned sample fails, empty frame fails")
    return True


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    command = sys.argv[1]

    if command == "width":
        for text in sys.argv[2:]:
            print(f"{text_width(text)}\t{text}")
        return 0

    if command == "selftest":
        import tempfile
        from pathlib import Path

        with tempfile.TemporaryDirectory(prefix="ttc-frame-selftest-") as directory:
            return 0 if selftest(Path(directory)) else 1

    if command == "align":
        separators = DEFAULT_SEPARATORS
        min_separators = 3
        require_blocks = 1
        frames = []
        rest = sys.argv[2:]
        index = 0
        while index < len(rest):
            argument = rest[index]
            if argument == "--chars":
                separators = rest[index + 1]
                index += 2
            elif argument == "--min-separators":
                min_separators = int(rest[index + 1])
                index += 2
            elif argument == "--require-blocks":
                require_blocks = int(rest[index + 1])
                index += 2
            else:
                frames.append(argument)
                index += 1

        if not frames:
            print("align: no frame given", file=sys.stderr)
            return 2

        ok = True
        for frame in frames:
            ok = align(frame, separators, min_separators, require_blocks) and ok
        return 0 if ok else 1

    print(f"unknown command: {command}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
