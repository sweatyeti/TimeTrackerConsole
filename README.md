# TimeTrackerConsole

C# .NET 10 console app for tracking time on tasks. Uses Spectre.Console + System.CommandLine.

## Quickstart

```bash
git clone https://github.com/sweatyeti/TimeTrackerConsole.git
cd TimeTrackerConsole
dotnet run -- new
```

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

## Menu

Single combined menu: summary table (task groups with counts/time) + admin/entry selector. In-progress entries highlighted green. A task's unlogged time is highlighted red in the summary. Logged/unlogged status shown.

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
Program.cs       — CLI entry point (System.CommandLine; `new` + `continue` commands); final store flush on exit
Session.cs       — Main loop, menus, entry CRUD, summary (Spectre.Console)
EntryStore.cs    — On-disk store: 5s periodic flush, dirty flag, atomic writes
TimeEntry.cs     — Data model (Id, StartTime, EndTime, Task, Description, Logged, IsComplete, IsDeleted)
```

## Design Note

Single-threaded, in-memory session UI; sessions are persisted to `entries/` by a background flush (see Persistence). A hard crash loses at most ~5 seconds of changes; graceful exit flushes everything. MIT license.
