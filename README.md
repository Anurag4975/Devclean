# DevClean

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Windows](https://img.shields.io/badge/platform-Windows-blue.svg)](#)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](#)

> A lightweight Windows app that finds what's eating your disk space — and helps you decide what can be removed **safely**.

DevClean scans your drives, shows you which files and folders are taking up space, and never deletes anything outright. Everything you choose to remove first goes into a **quarantine** folder, so you can restore it with one click — or permanently delete it only when you're sure.

---

## Features

### Drive scanning
- Scan any drive and get a full inventory of files **and** folders.
- Hidden folders are included in the scan — nothing is hidden from the inventory.
- Folder sizes are calculated recursively, including everything inside them.

### Files view
| Column | What it shows |
| --- | --- |
| File name | The file's name and path |
| Size | Exact size on disk |
| Type / category | Grouped by kind (cache, logs, temp, downloads, etc.) |
| Safety assessment | Whether it looks safe to remove |
| Reason | Why something might be removable |

### Folders view
- Folder name / full path
- Total size (recursive, including hidden contents)
- **Every** folder, including hidden folders — no complicated category filtering

### Smart selection
- Selecting a **parent** folder automatically excludes its children, so nothing is double-counted.
- Protected / system folders **cannot** be selected for deletion.

### Quarantine (safe delete)
Instead of immediately deleting, DevClean moves selected items to `.DevClean\Quarantine`:

```
User selects
   ↓
Safety check
   ↓
Move to .DevClean\Quarantine   ← separate handling for folders and files
   ↓
Item disappears from original location
   ↓
Quarantine Manager
   ↓
Restore to original location  OR  Permanently delete
```

- **Restore** — a quarantined file or folder is returned to its exact original location.
- **Permanent deletion** — items in quarantine can eventually be permanently deleted, including whole folders.

---

## How it works (end-to-end flow)

```
Drive
  ↓
Scan
  ↓
Files + Folders (with recursive sizes, hidden included)
  ↓
User selects folder(s) / file(s)
  ↓
Safety check (protected/system items blocked, parent/child de-duplicated)
  ↓
Quarantine (moved to .DevClean\Quarantine)
  ↓
Folder disappears from original location
  ↓
Quarantine Manager
  ↓
Restore  OR  Permanently delete
```

---

## Getting started

### Prerequisites
- Windows 10 / 11
- [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or later)

### Build and run

```powershell
cd D:\DevClean
dotnet build
dotnet run
```

> The first build restores NuGet packages. Run the app, pick a drive, and try scanning.

---

## Project structure

```
DevClean/
├── DevClean.sln
├── src/
│   ├── DevClean.App/          # Windows UI (WPF / WinForms — edit this line)
│   ├── DevClean.Core/         # Scanning, size calculation, safety assessment
│   └── DevClean.Quarantine/   # QuarantineService: move, restore, permanent delete
├── tests/
├── .gitignore
├── LICENSE
└── README.md
```

> Adjust the tree above to match your actual folder layout before pushing.

---

## Safety model

1. **Read-only scan.** Scanning never modifies anything.
2. **No immediate delete.** The only destructive-looking action is a *move* into quarantine.
3. **Protected paths.** System and protected folders are blocked from selection.
4. **Restore-first workflow.** Every quarantined item can be restored to its original path before any permanent deletion.
5. **Parent/child de-duplication.** Selecting a parent never counts its children separately.

---

## Roadmap / current status

- [x] Drive scan (files + folders, including hidden)
- [x] Recursive folder-size calculation
- [x] Files view with category, safety assessment, and removal reason
- [x] Folders view (all folders, no category filtering)
- [x] Smart selection (parent/child de-duplication, protected folders blocked)
- [x] Quarantine (separate file / folder handling)
- [x] Restore from quarantine
- [x] Permanent deletion from quarantine
- [ ] Integration testing of full folder-quarantine flow (in progress)
- [ ] Settings UI
- [ ] Scan history / growth trends

---

## Contributing

Issues and pull requests are welcome. The most valuable report is anything DevClean offered to remove that it should not have — please open an issue with the path and category.

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/thing`)
3. Commit (`git commit -m 'Add thing'`)
4. Push (`git push origin feature/thing`)
5. Open a Pull Request

---

## License

Distributed under the MIT License. See [`LICENSE`](LICENSE) for more information.

---

*Built with .NET. Made for Windows.*
