# TimeTrackerConsole

C# .NET 10 console app for tracking time on tasks. Uses Spectre.Console + System.CommandLine.

## Quickstart

```bash
git clone https://github.com/sweatyeti/TimeTrackerConsole.git
cd TimeTrackerConsole
dotnet run -- new
```

## Flags

| Flag | Description |
|------|-------------|
| `--old-menu` | Classic full-table view with numbered action picker |
| `--name <value>` | Optional session name |

## Menus

**New menu (default):** Summary table (task groups with counts/time) + combined admin/entry selector. In-progress entries highlighted green. Logged/unlogged status shown.

**Old menu (`--old-menu`):** Full entries table with all columns, summary table, then a numbered action picker.

## Actions

- Stop current entry and start a new one
- Update entry (toggle logged, edit task, edit description)
- Log an entire task group (marks all completed entries as logged)
- Delete entry (with confirmation)
- Stop tracking / Stop and exit

## Project Structure

```
Program.cs       — CLI entry point (System.CommandLine)
Session.cs       — Main loop, menus, entry CRUD, summary (Spectre.Console)
TimeEntry.cs     — Data model (Id, StartTime, EndTime, Task, Description, Logged, IsComplete)
```


## Design Note

Single-threaded, in-memory session. No persistence yet. MIT license.
