# TimeTrackerConsole

C# .NET 10 console app for tracking time on tasks. Uses Spectre.Console + System.CommandLine, with an opt-in Terminal.Gui interface (`--tui`).

## Quickstart

```bash
git clone https://github.com/sweatyeti/TimeTrackerConsole.git
cd TimeTrackerConsole
dotnet run -- new

# Alternate presentation theme
dotnet run -- new --theme anime
dotnet run -- continue --theme anime

# Terminal.Gui interface (opt-in)
dotnet run -- new --tui
dotnet run -- continue --tui
```

## Interfaces

`Spectre.Console` renders the UI by default. `--tui` (accepted by both `new` and `continue`) selects an opt-in `Terminal.Gui` interface instead: same menu actions, same prompts-then-act order, same guards, and the same `entries/*.json` session files — only the rendering and the prompt widgets differ. It is a flag, not a value; omitting it leaves the default interface exactly as it was.

Themes apply to both interfaces from the same role table. One presentation difference: the default interface tints the terminal's *default* background for the anime themes (see below), while the Terminal.Gui interface colours its own views and leaves the terminal's default colours untouched, so the tint covers the area the app draws on.

## Commands

| Command | Description |
|---------|-------------|
| `new` | Start a new session |
| `continue` | List previous sessions (newest first) and resume one; sessions that didn't exit cleanly are marked "(unfinished)" |

## Flags

| Flag | Description |
|------|-------------|
| `--name <value>` | Optional session name |
| `--page-size <n>` | Number of menu items shown before paging (default: 30) |
| `--theme <current\|anime\|anime-light>` | Presentation theme (default: `current`; accepted case-insensitively) |
| `--tui` | Use the opt-in Terminal.Gui interface instead of the default Spectre.Console one |

`current` preserves the existing palette. `anime` is an original whimsical pastoral-fantasy palette (vivid spring green, sky blue, gold, khaki and berry roles) on a dark pine ground; `anime-light` is the same direction on a bright parchment ground. On the default interface both anime themes tint the terminal's *default* background for the duration of the session and restore it on exit; `anime-light` also sets the default foreground, since its ground is light. Theme selection affects presentation only and is not stored in session files.

On the default interface the tinting uses `OSC 11`/`OSC 10` to set and `OSC 111`/`OSC 110` to reset the terminal's default colours, which xterm-style terminals, tmux (3.1+), Windows Terminal, iTerm2, VTE, Kitty, WezTerm, and Alacritty understand; terminals that don't simply ignore it, and it is never emitted when output is redirected. `current` leaves the terminal's own colours alone. The Terminal.Gui interface emits no OSC sequences at all — it colours its views and restores the screen when it exits.

## Menu

Single combined menu: summary table (task groups with counts/time) + admin/entry selector. In-progress entries highlighted green. A task's unlogged time is highlighted red in the summary. Logged/unlogged status shown.

The Terminal.Gui interface renders the same banner, summary, totals and entry list; its admin options are function keys F2–F6 with the same order and behavior as the console menu, and `Enter` on a row opens the update flow. Esc is inert in the main window (F6 stops tracking and exits); dialogs use Esc to cancel.

## Actions

- Stop current entry and start a new one
- Update entry (toggle logged, edit task, edit description)
- Log an entire task group (marks all completed entries as logged)
- Delete entry (completed entries only, from the edit-entry view — soft delete; the entry is kept in the store and only shown under "View deleted entries")
- View deleted entries (the only place deleted entries are visible; restore an individual entry here by flipping its flag back)
- Stop tracking / Stop and exit

## Persistence

Sessions are written to `entries/` as JSON — one file per session — by a background flush:

- **File name:** slug of the session name (spaces → `-`, Windows-invalid characters stripped); collisions get a `-2`, `-3` suffix
- **Write cadence:** dirty-flag + background timer flushes every 5 seconds; a final flush runs on graceful exit
- **Atomic writes:** each flush writes `<file>.tmp` then renames over the final file, so a crash never leaves a half-written session file
- **Schema:** self-describing envelope — `schemaVersion`, `sessionId`, `name`, `startedAt`, `endedAt`, `entries[]` — robust to renames and future imports

### Example `entries/<session>.json`

```json
{
  "schemaVersion": 2,
  "sessionId": "084120d5-51ab-4a6b-87e8-5a115adb0573",
  "name": "Smoke [Test]",
  "startedAt": "2026-08-22T01:22:46.4250603+00:00",
  "endedAt": null,
  "entries": [
    {
      "id": 1,
      "startTime": "2026-08-22T01:22:46.4339468+00:00",
      "endTime": null,
      "task": "task-one",
      "description": "",
      "logged": false,
      "isComplete": false,
      "isDeleted": false
    }
  ]
}
```

## Project Structure

```
Program.cs         — CLI entry point (System.CommandLine; `new` + `continue` commands); final store flush on exit
Session.cs         — Main loop, menus, entry CRUD, summary (Spectre.Console)
ConsoleTheme.cs    — Presentation theme role tables (`current`, `anime`, `anime-light`)
ConsoleBackdrop.cs — Applies/restores the theme's terminal default colours (OSC 10/11); default interface only
EntryStore.cs      — On-disk store: 5s periodic flush, dirty flag, atomic writes
TimeEntry.cs       — Data model (Id, StartTime, EndTime, Task, Description, Logged, IsComplete, IsDeleted)
SnapshotNormalizer.cs — Snapshot repair/validation shared by every load path (null Entries/Task/Description, duplicate ids)
Tui/               — Opt-in Terminal.Gui interface (`--tui`): window, layout, themed schemes, entry list, dialogs
tools/             — tmux behavioural smoke harnesses (`tui-smoke.sh`, `console-smoke.sh`) + fixtures
TimeTrackerConsole.Tests/ — xunit tests for the shared logic, the snapshot normalizer and the pure TUI helpers
```

## Design Note

Single-threaded, in-memory session UI; sessions are persisted to `entries/` by a background flush (see Persistence). A hard crash loses at most ~5 seconds of changes; graceful exit flushes everything. MIT license.
