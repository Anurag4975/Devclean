# DevClean Upgrade — Drop-In Files

Everything here compiles against your existing code. **No edits to MainWindow.xaml / MainWindow.xaml.cs are required.**

## Step 1 — Replace these 5 files (overwrite existing)

| File | What changed |
|---|---|
| `DevClean.csproj` | Versioning + copies `Rules/*.json` next to the .exe |
| `AI/LocalSafetyAnalyzer.cs` | Now backed by the rule database + learning loop. Same public signature. |
| `Safety/SafetyEngine.cs` | Now backed by the rule database. Same public signature. |
| `Cleanup/QuarantineService.cs` | Atomic metadata writes + .bak backup + learning loop on restore + `PurgeExpiredAsync` / `GetQuarantineSizeAsync`. Same public signatures. |
| `Candidates/CandidateDetector.cs` | Suppresses restored paths + rule-based score bonuses + age scoring. Same public signatures. |
| `.gitignore` | Proper Visual Studio ignore (stops bin/obj being committed). |

## Step 2 — Add these new files/folders (copy as-is)

```
Rules/safety-rules.json          ← external, editable rule database (community-extendable)
Safety/SafetyRuleDatabase.cs     ← rule loader (built-in defaults + JSON override)
Smart/UserPreferenceStore.cs     ← learning loop: restored paths are never suggested again
Smart/SmartCleanAdvisor.cs       ← one-click Smart Clean planner (Safe items only, dedup, plain-English summary)
Settings/AppSettings.cs          ← persisted settings (quarantine retention days, auto-purge, simple mode)
ModernJunk/ModernJunkScanner.cs  ← fast detection of Docker/WSL/npm/NuGet/iOS backups/Android SDK/Teams cache
```

## Step 3 — Build

```
dotnet build
```

If you see an error about `Rules/safety-rules.json`, make sure the `Rules` folder sits at the **project root** (next to `DevClean.csproj`), not inside a subfolder.

## What now works with ZERO UI changes

1. **Learning loop** — restore anything from quarantine → it will never be suggested again (stored in `%AppData%\DevClean\preferences.json`).
2. **Modern junk detection** — node_modules, browser caches (login-safe), package caches, Windows Update cache, etc. are now classified with high confidence and plain-English explanations.
3. **Hard protection** — `.git` repos, source code files, Program Files, WinSxS, hiberfil/pagefile are now `DoNotDelete` (cannot be selected).
4. **Corruption-proof quarantine** — metadata is written atomically with a .bak backup; a crash mid-write can no longer lose your quarantine index.
5. **Auto-expiry API ready** — `QuarantineService.PurgeExpiredAsync(days)` exists; wire it to a setting or a button when ready.

## Recommended one-line safety fix in MainWindow.xaml.cs (optional but strongly advised)

Your current `SelectAllCheckBox_Checked` selects **everything** that isn't DoNotDelete — including Review and Caution items. That is the #1 way users accidentally delete something important. Change the filter line from:

```csharp
.Where(x => x.Analysis.Level != SafetyLevel.DoNotDelete)
```

to:

```csharp
.Where(x => x.Analysis.Level == SafetyLevel.Safe && x.Analysis.Confidence >= 80)
```

Now "Select all" = "select everything safe to clean" — perfect for non-technical users. Power users can still tick Review items individually.

## How to add a one-click "Smart Clean" button (copy-paste)

Add a button in `MainWindow.xaml`, then in the click handler:

```csharp
var plan = DevClean.Smart.SmartCleanAdvisor.Plan(
    _allCandidates.Select(c => (c.Candidate, c.Analysis)));

MessageBox.Show(plan.Summary, "Smart Clean");

// Then quarantine plan.SafeItems exactly like your existing QuarantineButton_Click does.
```

## How to add a "Modern Junk" panel (copy-paste)

```csharp
var junk = DevClean.ModernJunk.ModernJunkScanner.Scan();
foreach (var item in junk.Where(j => j.Detected))
{
    // item.Name, item.Path, item.EstimatedSizeBytes,
    // item.Description, item.RecommendedAction, item.SafeToCleanInApp
}
```

## Remove bin/obj from git (they were accidentally committed)

```
git rm -r --cached bin obj
git commit -m "Remove build artifacts from version control"
```
