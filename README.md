# TimeTrackerConsole

A simple, elegant console application for tracking time spent on tasks, built with C# and [Spectre.Console](https://spectreconsole.net). Perfect for daily personal time‑tracking without leaving the terminal.

![Screenshot](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet) ![License](https://img.shields.io/badge/license-MIT-green) ![Version](https://img.shields.io/badge/version-0.1.0-blue)

## Features

* **Dual menu system** – choose between the classic table view (`--old-menu`) or the modern summary‑first interface.
* **Task‑based grouping** – automatically groups completed entries by task, showing total time and unlogged minutes.
* **Live entry reversal** – newest entries appear first in the main menu (configurable).
* **Interactive logging** – mark entries as logged with a single keystroke.
* **Zero configuration** – runs immediately; all data stays in memory for the session.
* **Clean, colorful UI** – thanks to Spectre.Console’s rich terminal components.

## Installation

1. Ensure you have the [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) or later installed.
2. Clone the repository:
   ```bash
   git clone https://github.com/sweatyeti/TimeTrackerConsole.git
   cd TimeTrackerConsole
   ```
3. Run the application:
   ```bash
   dotnet run -- new
   ```
   (Add `--old-menu` to use the classic table view.)

## Usage

### Starting a new session
```bash
dotnet run -- new
```
Optionally name your session:
```bash
dotnet run -- new --name "Project X"
```

### Menu navigation
| Key | Action |
|-----|--------|
| `n` | Start a new time entry |
| `u` | Update the currently‑in‑progress entry |
| `l` | Toggle logged status for a completed entry |
| `d` | Delete an entry |
| `q` | Quit the session |

### Example workflow
1. Launch with `dotnet run -- new`
2. Press `n` to start a new entry, enter a task description.
3. Work – the entry tracks elapsed time.
4. Press `u` to stop the entry, optionally add a description.
5. View the summary table showing total time per task.
6. Press `l` on a completed entry to mark it as logged (for external time‑sheets).
7. Quit with `q` when done.

## Screenshot

```
┌─────────────────────────────────────────────────────────┐
│                    Session 2026‑05‑27 14:30:00          │
├─────────────────────────────────────────────────────────┤
│  Summary                                                │
│  ┌─────────┬────────────┬────────────┬──────────────┐  │
│  │ Task    │ Entries    │ Total mins │ Unlogged mins│  │
│  ├─────────┼────────────┼────────────┼──────────────┤  │
│  │ coding  │ 3          │ 145        │ 45           │  │
│  │ meeting │ 1          │ 30         │ 0            │  │
│  └─────────┴────────────┴────────────┴──────────────┘  │
│                                                         │
│  [1] 14:05‑14:50  coding          (In progress)        │
│  [2] 13:30‑14:00  meeting         logged               │
│  [3] 12:00‑13:25  coding          unlogged             │
│                                                         │
│  (n) new entry   (u) update entry (l) toggle logged    │
│  (d) delete entry (q) quit                             │
└─────────────────────────────────────────────────────────┘
```

## Project structure

```
TimeTrackerConsole/
├── Program.cs          # CLI entry point (System.CommandLine)
├── Session.cs          # Core session logic, menus, summary
├── TimeEntry.cs        # Time entry model
└── TimeTrackerConsole.csproj
```

### Key design decisions
* **Single‑threaded simplicity** – uses `Dictionary<int, TimeEntry>` instead of `ConcurrentDictionary` because the workflow is linear.
* **Separation of display and data** – the `Session` class handles both UI and business logic, keeping the model lightweight.
* **Two menu styles** – the “new” menu shows the summary first, then entries; the “old” menu shows the full table. Switch with `--old-menu`.

## Development

### Building
```bash
dotnet build
```

### Running tests
*(No test suite yet – contributions welcome!)*

### Adding features
1. Fork the repository.
2. Create a feature branch (`feat/your‑feature`).
3. Implement, ensuring the code compiles (`dotnet build`).
4. Submit a pull request.

## Contributing

Pull requests are welcome! Please keep changes focused and ensure the project builds without warnings.

### Style guidelines
* Follow existing C# naming conventions.
* Prefer clarity over cleverness.
* Add XML doc comments for public methods.

## License

MIT © Mathias (sweatyeti). See [LICENSE](LICENSE) for details.

---

*Built with ☕ and [Spectre.Console](https://spectreconsole.net) – because tracking time shouldn’t take time.*