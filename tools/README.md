# tools/ - behavioural smoke harnesses

Two tmux-driven harnesses, one per interface. They exist because the unit tests can prove the state
machine but not that the *screen* is right: every TUI defect found so far (blank status bar after an
action, Enter going to row 0, a stale table column, a clipped banner, an unhandled
`NullReferenceException` on a hand-edited session) needed a real terminal to be seen.

| Script | Interface | Checks |
|---|---|---|
| `tui-smoke.sh` | `--tui` (Terminal.Gui) | 53 |
| `console-smoke.sh` | default (Spectre.Console) | 28 |

Both build the app (unless `--no-build`), create a disposable scratch directory under `$TMPDIR`,
generate their session fixtures there with `tui-fixtures.py`, run the app **from that directory** so
`entries/` resolves inside the scratch dir (the repo's own `entries/` is never read or written), and
print one `PASS`/`FAIL` line per check plus a summary, exiting non-zero if any check failed. Frames
are left in the scratch directory (its path is printed) for inspection.

`TTC_SMOKE_DLL=/path/to/TimeTrackerConsole.dll` points either script at a pre-built DLL - which is
how the same checks are run against a baseline commit to show what a change actually fixes.

## `tui-smoke.sh`

```bash
tools/tui-smoke.sh              # build, then run every scenario
tools/tui-smoke.sh --no-build   # reuse bin/Debug/net10.0/TimeTrackerConsole.dll
tools/tui-smoke.sh --only narrow
```

| Scenario | Check |
|---|---|
| long task | editing a task to something wider renders in full immediately - no resize or second action needed |
| selection | Down/Down then F5: Enter afterwards still names the same entry id (the refresh must not reset the selection to row 0) |
| 44 groups | at 120x40 the totals line is on screen, at least 3 entry rows are visible, and Ctrl+End reaches the oldest entry |
| corrupt files | the session picker offers the readable `task: null` session and the session with no `entries` key (both normalize and render), while a `schemaVersion: 99` file and files whose `schemaVersion` is missing (`0`), explicitly `0`, or negative are **not offered** and are each named with an actionable reason after exit; no frame contains an unhandled exception |
| Esc | Esc is inert in the main window, cancels a dialog, and the window survives |
| narrow | at 60x24 the totals line fits, all five shortcuts are on the status bar with compact titles, the banner is the 55-column block (the uncapped 71-column block wraps), and the F2 dialog's box fits inside 60 columns |
| unicode | CJK / emoji / combining-mark values in the task *and* description column render without an exception, and at both 120x40 and 60x24 **every row of the entry table and of the summary table puts its column separators in the same display columns** as the ASCII control row (the observed frame is printed for the record) |

## `tools/tui-frame.py` - display-column frame checks

`tmux capture-pane` returns the *cells* of the screen as text, so the character count of a captured
line is not the number of terminal columns it occupies: a CJK ideograph is one character and two
columns, a combining acute accent is one character and zero columns. A table row containing wide text
therefore always looks "shifted" against an ASCII row when the frame is measured in characters, even
when the terminal shows the columns perfectly aligned.

`tui-frame.py` measures display columns - what the reader sees - and the `unicode` scenario asserts
with it:

```bash
python3 tools/tui-frame.py align <frame> [--require-blocks 2] [--chars '|'] [--min-separators 3]
python3 tools/tui-frame.py width '計画レビュー' 'e<U+0301>' '🚀'
python3 tools/tui-frame.py selftest        # width model + both verdict directions
```

`align` groups *contiguous* frame rows that draw at least three separators into table blocks (blank
filler rows carry only the two window-border columns, so they are ignored) and requires every row of a
block to place its separators in the same columns; `--require-blocks` makes a frame with fewer tables
than expected fail, so the check cannot pass by finding nothing to compare. `tui-smoke.sh` runs
`selftest` before any scenario: 15 strings whose expected widths were measured against the terminal
itself (the pane cursor column after printing the string), an aligned sample that must pass, the two
misaligned rows quoted in the wide-column ticket that must fail, and an empty frame that must fail.

## `console-smoke.sh`

```bash
tools/console-smoke.sh                    # build, then run every scenario
tools/console-smoke.sh --only actions     # actions | new | schema | mixedcase
```

It covers the default interface because the review fixes touched shared `Session` logic (snapshot
repair, what `StopCurrentEntry` stops, the unlogged-task-group projection) and `Program.cs`:

| Scenario | Check |
|---|---|
| actions | every admin menu option in order on a resumed session: the summary renders, logging a task group clears its unlogged column, "view deleted entries" reports an empty set, "stop tracking" ends the in-progress entry, "stop and exit" prints the final tables and exits |
| actions | a `schemaVersion: 99` session file is reported with its reason and is not offered |
| new | `new --name` takes an inline first task, shows it on the entry row, and writes a session file |
| schema | with a supported session in the same directory, files whose `schemaVersion` is missing (`0`), explicitly `0`, or negative are reported with an actionable reason and are not offered; the supported session still resumes |
| mixedcase | two entries whose task differs only in case are offered as ONE task group |

## Comparing against a baseline

```bash
git worktree add /tmp/ttc-baseline <base-commit>
dotnet build -v q --nologo /tmp/ttc-baseline/TimeTrackerConsole.csproj
TTC_SMOKE_DLL=/tmp/ttc-baseline/bin/Debug/net10.0/TimeTrackerConsole.dll tools/tui-smoke.sh --no-build
```

Both runs write only into their own scratch directory, so they cannot interfere.

Recorded results:

- `be3eca33` (the review base, with the suite as it stood at that revision: 35 TUI / 16 console checks):
  the TUI suite failed 6 of 35 (stale columns, selection reset, no viewport budget, an unhandled
  `NullReferenceException` on a null-task session, F6 clipped at 60 columns); the console suite failed
  5 of 16 (the missing skip report, the duplicated mixed-case group, and three `actions` checks that
  followed from that run selecting the unvalidated `schemaVersion: 99` session).
- `185be88` (this branch before the schema-version range check, full current suite): TUI **8 of 46**
  fail and console **8 of 28** fail - all of them the new schema-version checks, because the picker
  offered the `schemaVersion`-missing/`0`/negative files and the skip report never named them. Nothing
  else in either suite diverges from the fixed build, which is what makes those checks the
  discriminating evidence for the range validation.
- `d9c0e5b` (`dev`, the base of the wide-column branch, run with the widened fixture set and the new
  alignment checks): TUI **53 of 53** pass, alignment assertions included - the wide-character column
  shift that follow-up ticket `t_d6507589` reported for CJK/emoji rows is **not present in `dev`**.
  The same assertion run against the two rows quoted in that ticket reports them misaligned
  (separators at `[0,1,4,19,39,48,72]` and `[0,1,4,17,37,46,70]`); their right borders do not line up
  either, which no faithful `capture-pane` frame can do, so the quoted frame is not a verbatim capture
  and its apparent shift is what character counting measures, not what the terminal shows.
  The implementation that *did* count characters is the pre-TableView entry list (`f96253e^`): it
  renders the CJK task truncated there, and dies with `System.ArgumentOutOfRangeException` from
  `EntryListDataSource.Render` on the emoji's surrogate pair - both fixed by the TableView switch.
