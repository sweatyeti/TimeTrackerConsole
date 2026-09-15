# tools/ - behavioural smoke harnesses

Two tmux-driven harnesses, one per interface. They exist because the unit tests can prove the state
machine but not that the *screen* is right: every TUI defect found so far (blank status bar after an
action, Enter going to row 0, a stale table column, a clipped banner, an unhandled
`NullReferenceException` on a hand-edited session) needed a real terminal to be seen.

| Script | Interface | Checks |
|---|---|---|
| `tui-smoke.sh` | `--tui` (Terminal.Gui) | 35 |
| `console-smoke.sh` | default (Spectre.Console) | 16 |

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
| corrupt files | a `task: null` session and a session with no `entries` key both open and render; a `schemaVersion: 99` file is not offered and is named with its reason after exit; no frame contains an unhandled exception |
| Esc | Esc is inert in the main window, cancels a dialog, and the window survives |
| narrow | at 60x24 the totals line fits, all five shortcuts are on the status bar with compact titles, the banner is the 55-column block (the uncapped 71-column block wraps), and the F2 dialog's box fits inside 60 columns |
| unicode | CJK task and a combining-mark/emoji description render without an exception (the observed frame is printed for the record) |

## `console-smoke.sh`

```bash
tools/console-smoke.sh                    # build, then run every scenario
tools/console-smoke.sh --only actions     # actions | new | mixedcase
```

It covers the default interface because the review fixes touched shared `Session` logic (snapshot
repair, what `StopCurrentEntry` stops, the unlogged-task-group projection) and `Program.cs`:

| Scenario | Check |
|---|---|
| actions | every admin menu option in order on a resumed session: the summary renders, logging a task group clears its unlogged column, "view deleted entries" reports an empty set, "stop tracking" ends the in-progress entry, "stop and exit" prints the final tables and exits |
| actions | a `schemaVersion: 99` session file is reported with its reason and is not offered |
| new | `new --name` takes an inline first task, shows it on the entry row, and writes a session file |
| mixedcase | two entries whose task differs only in case are offered as ONE task group |

## Comparing against a baseline

```bash
git worktree add /tmp/ttc-baseline <base-commit>
dotnet build -v q --nologo /tmp/ttc-baseline/TimeTrackerConsole.csproj
TTC_SMOKE_DLL=/tmp/ttc-baseline/bin/Debug/net10.0/TimeTrackerConsole.dll tools/tui-smoke.sh --no-build
```

Both runs write only into their own scratch directory, so they cannot interfere. Recorded result on
`be3eca33`: the TUI suite fails 6 of 35 checks (stale columns, selection reset, no viewport budget,
an unhandled `NullReferenceException` on a null-task session, F6 clipped at 60 columns). The console
suite fails 5 of 16: the missing skip report, the duplicated mixed-case group, and three checks in the
`actions` scenario that follow from that run selecting the unvalidated `schemaVersion: 99` session
(pre-fix it is offered, so no summary/entries are on screen).
