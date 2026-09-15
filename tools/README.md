# tools/ - behavioural smoke harness for `--tui`

`tui-smoke.sh` drives the built Terminal.Gui interface through tmux and asserts on the rendered
frame. It exists because the unit tests can prove the state machine but not that the *screen* is
right: every TUI defect found so far (blank status bar after an action, Enter going to row 0, a
stale table column, a clipped banner) needed a real terminal to be seen.

## Running it

```bash
tools/tui-smoke.sh              # build, then run every scenario
tools/tui-smoke.sh --no-build   # reuse bin/Debug/net10.0/TimeTrackerConsole.dll
```

Every run:

* builds the app project (unless `--no-build`),
* creates a disposable scratch directory under `$TMPDIR`,
* generates its session fixtures there with `tui-fixtures.py`,
* runs `dotnet <dll> continue --tui` / `new --tui` **from that scratch directory**, so `entries/`
  resolves inside the scratch dir and the repo's own `entries/` is never read or written,
* prints one `PASS`/`FAIL` line per check plus a summary, and exits non-zero if any check failed.

Captured frames are left in the scratch directory (the path is printed at the end) so a failing
check can be inspected.

## What it asserts

| Scenario | Check |
|---|---|
| long task | F2 with a task wider than anything on screen renders in full immediately - no resize or second action needed |
| selection | Down/Down then F5: Enter afterwards still names the same entry id (the refresh must not reset the selection to row 0) |
| 44 groups | at 120x40 the totals line is on screen, at least 3 entry rows are visible, and Ctrl+End reaches the oldest entry |
| corrupt files | a `task: null` session and a session with no `entries` key both open and render; a `schemaVersion: 99` file is not offered and is named with its reason after exit; no frame contains an unhandled exception |
| Esc | Esc is inert in the main window, cancels a dialog, and the window survives |
| narrow | at 60x24 the totals line fits, all five shortcuts are on the status bar with compact titles, and the banner block is unclipped (widest line 40-55 columns) |
| unicode | CJK task and a combining-mark/emoji description render without an exception (the observed frame is printed for the record) |

## Comparing against a baseline

The harness reads text, so the same script can be pointed at a pre-change build to show which checks
a change actually fixes:

```bash
git worktree add /tmp/ttc-baseline <base-commit>
dotnet build -v q --nologo /tmp/ttc-baseline/TimeTrackerConsole.csproj
TTC_SMOKE_DLL=/tmp/ttc-baseline/bin/Debug/net10.0/TimeTrackerConsole.dll tools/tui-smoke.sh --no-build
```

The baseline run always uses that checkout's fixture generator path, but both runs write only into
their own scratch directory, so the two runs cannot interfere.
