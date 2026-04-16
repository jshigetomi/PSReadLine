---
name: sqlite-history
description: "Use when: implementing, debugging, or modifying SQLite-based history in PSReadLine. Covers database schema, history recall ordering, native library deployment, framework migration, F2 list view, migration timestamps, and cross-platform compatibility."
argument-hint: SQLite history implementation tasks, recall ordering, native library issues, or migration questions
---

# PSReadLine SQLite History Implementation

**Last Updated**: April 14, 2026
**Status**: Active — Built on .NET 8.0
**Target**: PowerShell 7.4+ (LTS) compatibility

---

## Executive Summary

PSReadLine's SQLite history replaces text-file history with a normalized database (Commands, Locations, ExecutionHistory tables). The migration from netstandard2.0 to net8.0 was required because Microsoft.Data.Sqlite needs .NET 6+ for proper native library deployment — netstandard2.0 doesn't populate `runtimeTargets` in deps.json and lacks `NativeLibrary.SetDllImportResolver`.

**Current State**: PSReadLine targets net8.0 with automatic runtime deployment.
**Breaking Change**: PSReadLine 3.0 requires PowerShell 7.4+ (LTS). Users on older versions must stay on PSReadLine 2.x.

## Key Decisions (April 2026)

### History Recall Ordering
- **Up/Down** (`HistoryRecall`): Pure chronological, DB-side dedup via `ROW_NUMBER()`. In-memory dedup via `HistoryNoDuplicates`.
- **Alt+Up/Down** (`LocationHistoryRecall`): Pre-sorted weighted index — **per-location** frequency DESC, then recency DESC. Only commands matching `$PWD`. "Unknown" entries excluded.
- **F2 List** (SQLite mode): Top half = current-dir by per-location frecency, bottom half = global by total frecency, with backfill. Plugins-active (3 slots) = total frecency only.
- **Text mode**: Unchanged — pure recency.

### ExecutionCount
- `HistoryItem.ExecutionCount` = SUM across all locations (total usage).
- All three SQL read paths use `SUM(ExecutionCount)`. Tooltip "Runs N" shows this total.
- **Local sorting** uses per-location counts via `GetLocationExecutionCounts()` — a command run once here but 100× elsewhere shouldn't dominate.

### History Deletion
- **Alt+Delete** (`RemoveFromHistory`): Default binding in Windows & Emacs modes. Removes from memory + SQLite. Advances to next older item. Also works in F2 list.
- Previously bound to `KillWord` (still on `Alt+D` / `Ctrl+Delete`).

### Timestamps
- Stored as Unix seconds (`ToUnixTimeSeconds()`), read with `FromUnixTimeSeconds()`).
- Do NOT use .NET ticks in ad-hoc SQL queries.

---

## Critical Gotchas

These are high-value traps — read before making any changes.

### 1. Stale DLL Pitfall
Incremental `./build.ps1` may NOT refresh published DLLs. If code changes aren't taking effect:
```powershell
./build.ps1 -Clean
./build.ps1
```
PowerShell caches assemblies in-process — **restart all pwsh sessions** after deploying a new DLL.

### 2. ReadLine Main Loop Counter Requirement
The main loop in `ReadLine.cs` (~lines 540-650) saves counters before each key press; if unchanged, it resets state (e.g., `_currentHistoryIndex = _history.Count`). Any history-related key handler **MUST** increment `_recallHistoryCommandCount` and/or `_anyHistoryCommandCount`.

### 3. C# 9 Readonly Struct Field Bug
`SuggestionEntry` is a `struct` with `LangVersion 9.0`. Assigning a `readonly` field in a constructor body after `: this(...)` chaining **silently doesn't take effect**. The `HistoryItemRef` constructor MUST set all fields directly — no constructor chaining.

### 4. `_saved` Flag on Loaded Items
`ReadSQLiteHistory` and `ReadHistorySQLiteIncrementally` must set `_saved = true` on all loaded HistoryItems. Without this, `IncrementalHistoryWrite` re-writes them with `LastExecuted = UtcNow`, destroying original timestamps.

### 5. VT Attribute Pitfall (Tooltip Rendering)
`_listPredictionTooltipColor` defaults to `\x1b[97;2;3m` (bright white + dim + italic). VT SGR attributes are **additive** — appending `\x1b[2m` does NOT cancel italic. Must use `\x1b[0m\x1b[2m` (full reset, then dim-only) before each icon character.

### 6. Migration Timestamp Ordering
Migrated items must all have timestamps older than "now". Assign timestamps **after** collecting all items: `migrationBase = UtcNow - (Count+1) minutes`, each item gets `migrationBase.AddMinutes(idx)`. Do NOT assign during collection (inverts order).

### 7. Native DLL Layout
PowerShell's `NativeDllHandler` expects flat `{rid}/{libraryName}`, NOT NuGet's `runtimes/{rid}/native/`. Always use `./build.ps1` — manual `dotnet build` output has wrong layout.

---

## Build Commands

```powershell
# Build and publish (build script handles native DLL restructuring)
./build.ps1

# Clean build — required when incremental builds don't refresh DLLs
./build.ps1 -Clean
./build.ps1

# Manual build (runtimes/ stays in NuGet layout — no restructuring)
dotnet build PSReadLine/PSReadLine.csproj -c Debug
dotnet publish PSReadLine/PSReadLine.csproj -c Debug
```

`dotnet build`/`publish` produces the NuGet `runtimes/{rid}/native/` layout. The `BuildMainModule` task in `PSReadLine.build.ps1` restructures this into the flat `{rid}/` layout after publish.

---

## Implementation Checklist

### Framework ✅ COMPLETE
- [x] `<TargetFramework>net8.0</TargetFramework>`
- [x] `PSReadLine.psd1`: `PowerShellVersion = '7.4'`
- [x] SQLite packages at version 2.1.11+
- [x] Runtime assets auto-deploy

### Code ✅ COMPLETE
- [x] `NativeLibrary.SetDllImportResolver` for native library resolution
- [x] `RuntimeInformation.RuntimeIdentifier` for platform detection
- [x] Single target framework — no conditional compilation

### History Recall ✅ COMPLETE
- [x] `ReadSQLiteHistory`: Chronological + deduplicated SQL (`ROW_NUMBER`)
- [x] `HistoryRecall` (Up/Down): Walks `_history` backward, dedup via `HistoryNoDuplicates`
- [x] `LocationHistoryRecall` (Alt+Up/Down): Per-location frequency + recency
- [x] `RemoveFromHistory` (Alt+Delete): Memory + SQLite removal
- [x] F2 list view: Location-partitioned frecency + stats tooltip

### Testing — TODO
- [ ] Build and test on Windows/Linux/macOS
- [ ] Test with PowerShell 7.4+ LTS
- [ ] Verify native library loading on all platforms
- [ ] Test history migration from text files

### Documentation — TODO
- [ ] Update README: minimum PowerShell 7.4+ requirement
- [ ] Add release notes: breaking change for PS <7.4 users

---

## Verification

After loading a new build, confirm expected methods exist:
```powershell
[Microsoft.PowerShell.PSConsoleReadLine].GetMethods(
    [System.Reflection.BindingFlags]'NonPublic, Instance'
) | Where-Object { $_.Name -match 'Location' } | ForEach-Object { "$($_.Name)($($_.GetParameters().Count) params)" }
```

---

## Reference Files

Load these for detailed implementation specifics:

- [Architecture & Native Libraries](./references/architecture.md) — Framework migration rationale, native library resolution, module distribution layout, supported RIDs
- [Schema & History Logic](./references/schema-and-history.md) — Database schema, recall ordering implementation, F2 list view, tooltip rendering, deletion flow, migration
- [API Surface & Code Map](./references/api-surface.md) — Enumerations, cmdlet parameters, path properties, Options.cs switching, file-level cross-references

## External Resources

- [Microsoft.Data.Sqlite Documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
- [SQLitePCLRaw GitHub](https://github.com/ericsink/SQLitePCL.raw)
- [.NET Native Library Loading](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [PowerShell Module Development](https://learn.microsoft.com/en-us/powershell/scripting/developer/module/writing-a-windows-powershell-module)
