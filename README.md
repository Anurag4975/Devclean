# DevClean — Safe Disk Cleanup with Undo

A Windows disk cleaner that **never deletes anything outright**. Everything you remove first goes into a **quarantine** folder with one-click restore. Built for non-technical users, powerful enough for developers.

## What makes it different from CCleaner / BleachBit / WinDirStat

- **Undo on everything** — no other cleaner has a full quarantine + restore. BleachBit has no undo; CCleaner deletes immediately.
- **Smart Clean** — one button selects *only* items rated Safe with high confidence. No scary checkboxes.
- **Simple mode** — hides all filters, auto-selects what's safe. For non-technical users.
- **Learning loop** — if you restore something, DevClean never suggests it again.
- **Modern junk detection** — Docker/WSL virtual disks, npm/NuGet caches, iOS backups, Android SDK, Teams cache — things old cleaners ignore.
- **External rule database** — `Rules/safety-rules.json` is editable and community-extendable; rules update without an app update.
- **Hard protection** — `.git` repos, source code, Program Files, WinSxS, pagefile/hiberfil can never be selected.
- **Corruption-proof quarantine** — atomic metadata writes with automatic .bak backup.
- **No background service, no bundling, no telemetry.** Open core.

## Features

- Drive scan (files + folders, hidden included, recursive sizes)
- Files + Folders views with category, safety assessment, confidence, and plain-English reason
- Smart parent/child selection (no double-counting)
- Quarantine → restore / permanent delete
- Free-space gauge, live before/after
- Modern Junk panel (detection + advice, no auto-deletion)
- Protected paths blocked from selection

## Build & run

```
dotnet build
dotnet run
```

Requires .NET 8+ (project targets net10.0-windows). Windows 10/11.

## Project structure

```
AI/            LocalSafetyAnalyzer (rule DB + learning loop)
Candidates/    CandidateDetector (scoring + suppression)
Cleanup/       QuarantineService (atomic, auto-expiry API) + CleanupResult
Models/        FileItem, CleanupCandidate, SafetyResult, QuarantineItem, ...
Safety/        SafetyEngine, LocationClassifier, SafetyRuleDatabase + Rules/safety-rules.json
Scanner/       DiskScanner (parallel, reparse-point safe)
Smart/         UserPreferenceStore (learning loop), SmartCleanAdvisor
Settings/      AppSettings (quarantine retention, auto-purge, simple mode)
ModernJunk/    ModernJunkScanner (Docker, WSL, caches, iOS backups, ...)
Rules/         safety-rules.json (external, community-extendable)
```

## Safety model

1. Read-only scan — never modifies anything.
2. No immediate delete — the only action is a *move* into quarantine.
3. Protected paths cannot be selected.
4. Restore-first — every quarantined item restores to its exact original path.
5. Parent/child de-duplication — selecting a parent never double-counts children.
6. Learning loop — restored paths are never suggested again.

## Roadmap

- [ ] Scheduled auto-clean (safe categories only) — Pro
- [ ] Duplicate file/photo finder — Pro
- [ ] Large-file spotlight with preview — Pro
- [ ] Microsoft Store MSIX packaging
- [ ] Scan history / growth trends ("what grew this month?")
- [ ] Treemap visualization

## License

MIT — core engine open. (Pro features may be kept closed for monetization.)
