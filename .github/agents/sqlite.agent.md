---
name: sqlite
code description: Expert agent for implementing SQLite-based history in PSReadLine, covering .NET framework migration, native library deployment, and cross-platform compatibility.
argument-hint: SQLite implementation tasks, migration questions, or troubleshooting help
tools: [vscode, execute, read, agent, edit, search, web, browser, todo]
---

# PSReadLine SQLite History Implementation Guide

**Last Updated**: April 14, 2026  
**Status**: Active - Built on .NET 8.0  
**Target**: PowerShell 7.4+ (LTS) compatibility

---

## Executive Summary

This guide documents SQLite-based history implementation for PSReadLine. **PSReadLine is now built on .NET 8.0**, aligned with PowerShell 7.4 LTS. The migration from netstandard2.0 to net8.0 was necessary because Microsoft.Data.Sqlite requires .NET 6+ for proper native library deployment - netstandard2.0 doesn't properly deploy platform-specific native DLLs and lacks the APIs needed to resolve them at runtime.

**Current State**: PSReadLine targets net8.0 with automatic runtime deployment.

---

## Why netstandard2.0 Fails with SQLite

### The Technical Issue

**deps.json Missing Runtime Assets**:

When building for netstandard2.0, the generated `deps.json` contains:
```json
"SQLitePCLRaw.lib.e_sqlite3/2.1.10": {
  "runtimeTargets": {}  // ΓåÉ EMPTY - no native DLLs listed
}
```

When building for net6.0+:
```json
"SQLitePCLRaw.lib.e_sqlite3/2.1.10": {
  "runtimeTargets": {
    "runtimes/win-x64/native/e_sqlite3.dll": { "rid": "win-x64", "assetType": "native" },
    "runtimes/linux-x64/native/libe_sqlite3.so": { "rid": "linux-x64", "assetType": "native" }
  }
}
```

**Why This Matters**:
- .NET runtime uses `deps.json` to know which native libraries exist and where to find them
- Without `runtimeTargets`, native DLLs aren't copied to output automatically
- `DllImport("e_sqlite3")` fails because runtime doesn't know where to search
- Manual copying doesn't help - runtime still doesn't know about the files

**Missing APIs**:
- netstandard2.0 lacks `NativeLibrary.SetDllImportResolver` (requires .NET 5+)
- No way to customize native library search paths
- P/Invoke fallbacks (LoadLibrary/dlopen) don't register with .NET's import system

---

## Framework Migration Strategy

### Current Implementation

**PSReadLine now targets .NET 8.0 exclusively**:

```xml
<TargetFramework>net8.0</TargetFramework>  <!-- PowerShell 7.4+ LTS -->
```

### PowerShell/Runtime Compatibility

| PowerShell | .NET Runtime | Support Status | Works with net6.0? | Works with net8.0? |
|-----------|--------------|----------------|-------------------|-------------------|
| 5.1 | .NET Framework 4.7.2 | Indefinite | Γ¥î No | Γ¥î No |
| 7.0 | .NET Core 3.1 | Ended Dec 2022 | Γ£à Yes | Γ£à Yes |
| 7.2 LTS | .NET 6.0 | Ended Nov 2024 | Γ£à Yes | Γ£à Yes |
| 7.4 LTS | .NET 8.0 | Until Nov 2026 | Γ£à Yes | Γ£à Yes |

### Why .NET 8.0?

**Benefits of .NET 8.0**:
- Aligns with PowerShell 7.4 LTS (supported until November 2026)
- .NET 6 support has ended (November 2024)
- Single build target simplifies testing and deployment
- Native library deployment is fully automatic
- Full API support for SQLite native library resolution

**Breaking Change**: Users on PowerShell 5.1/7.0/7.1/7.2 must stay on PSReadLine 2.x.

---

## Implementation Files Overview

### 1. PSReadLine.csproj
**Purpose**: Project configuration and dependency management

**Current Configuration**:
```xml
<TargetFramework>net8.0</TargetFramework>

<!-- SQLite packages -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.4" />
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.11" />
```

**Result**: Native libraries deploy to `bin/Debug/net8.0/runtimes/{rid}/native/` during build. The build script (`PSReadLine.build.ps1`) then restructures these into the flat `{rid}/` layout that PowerShell's `CorePsAssemblyLoadContext.NativeDllHandler` expects.

### 2. History.cs
**Purpose**: SQLite database initialization and native library resolution

**Native Library Resolution (net6.0+)**:
```csharp
private void SetupSQLiteNativeLibraryResolver()
{
    NativeLibrary.SetDllImportResolver(
        typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly, 
        (libraryName, assembly, searchPath) =>
        {
            if (libraryName.Equals("e_sqlite3", StringComparison.OrdinalIgnoreCase))
            {
                var possiblePaths = GetSQLiteLibraryPaths();
                foreach (var path in possiblePaths)
                {
                    if (NativeLibrary.TryLoad(path, out IntPtr handle))
                        return handle;
                }
            }
            return IntPtr.Zero;
        });
}

private string[] GetSQLiteLibraryPaths()
{
    var moduleRoot = Path.GetDirectoryName(typeof(PSConsoleReadLine).Assembly.Location);
    var rid = RuntimeInformation.RuntimeIdentifier; // e.g., "win-x64", "linux-x64"
    var libName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "e_sqlite3.dll" :
                  RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "libe_sqlite3.so" :
                  "libe_sqlite3.dylib";
    
    return new[] {
        Path.Combine(moduleRoot, rid, "native", libName),
        Path.Combine(moduleRoot, libName)
    };
}
```

**Database Schema**: Normalized structure with Commands, Locations, and ExecutionHistory tables with foreign keys and indexes.

**History Recall Ordering** (decided April 2026):
- `ReadSQLiteHistory()` loads history **chronologically** (`LastExecuted DESC`) with **per-CommandLine deduplication** via `ROW_NUMBER() OVER (PARTITION BY CommandLine ORDER BY LastExecuted DESC)`. This ensures basic Up/Down recall is simple reverse-chronological with no duplicates.
- `LocationHistoryRecall()` builds a **weighted sorted index** on first press: filtered to current location, then sorted by **per-location** `ExecutionCount DESC` (frequency at this directory), then by position DESC (recency). In SQLite mode, queries per-location counts via `GetLocationExecutionCounts()` so that a command run once here but 100 times elsewhere doesn't dominate. Falls back to total `ExecutionCount` in Text mode.
- `HistoryRecall()` relies on `HistoryNoDuplicates` option for in-memory dedup (the SQL already deduplicates DB-loaded entries).

**History Deletion** (April 2026):
- **Alt+Delete** (`RemoveFromHistory`): Default binding in both Windows and Emacs modes. While browsing history with Up/Down, removes the currently displayed entry from in-memory history and the SQLite database (if using SQLite mode). After deletion, advances to the next **older** item (same direction as Up arrow) so the user can keep pressing Alt+Delete to delete consecutive items. Also works in the F2 prediction list view.
- `RemoveHistoryItem(string)`: Programmatic API to remove a specific command by text.
- `ClearHistory` (Alt+F7 on Windows): Clears all history.
- **Note**: Alt+Delete was previously bound to `KillWord`; that function remains available via `Alt+D` and `Ctrl+Delete` (Windows).
- **Critical**: `RemoveFromHistory` must increment `_recallHistoryCommandCount` and `_anyHistoryCommandCount` so the ReadLine main loop doesn't reset `_currentHistoryIndex` after the key press.

**Database Initialization**:
- `InitializeSQLiteDatabase(bool migrateTextHistory = false)` ΓÇö creates schema if new DB
- `migrateTextHistory: true` only on initial Text ΓåÆ SQLite switch (not when relocating DB)
- Migration reads from `_options.HistorySavePathText` directly (no path derivation hacks)

**Text-to-SQLite Migration Timestamps**:
- Migrated items must all have timestamps **older** than "now" so they sort before any new SQLite entries
- Timestamps are assigned after collecting all items: `migrationBase = UtcNow - (Count+1) minutes`, each item gets `migrationBase.AddMinutes(idx)` ΓÇö oldest text line gets earliest timestamp
- Do NOT assign timestamps during collection (the growing count inverts the order)

**`_saved` Flag on Loaded Items**:
- `ReadSQLiteHistory` and `ReadHistorySQLiteIncrementally` must set `_saved = true` on all loaded HistoryItems
- Without this, `IncrementalHistoryWrite` re-writes loaded items with `LastExecuted = UtcNow`, destroying original timestamps and scrambling order

**F2 List View History Stats Tooltip** (April 2026):
- When `HistoryType` is `SQLite`, selecting a history item in the F2 prediction list view shows a colored stats tooltip beneath the selected entry
- Tooltip format: `Γ¥»Γ¥» Γƒ│ Runs 47  Γöé  ΓÅ▒ Last 2m ago  Γöé  ≡ƒôé Dir ~/repos/PSReadline`
- Icons with short text labels for accessibility (screen readers read the label; icons are visual decoration)
- Labels rendered in dim/italic tooltip color, values in the highlight/accent color, separators dimmed
- Fields shown: `ExecutionCount` (total across all locations via SUM), `StartTime` (relative time), `Location` (if not "Unknown")
- **Custom renderer**: `RenderHistoryStatsTooltip(HistoryItem)` writes colored output directly to buffer lines ΓÇö cannot use generic `RenderTooltip` because it iterates char-by-char and treats VT escape chars as control characters (renders `^[`)
- `FormatHistoryStatsTooltip(HistoryItem)` generates plain-text version used as non-null `ToolTip` trigger
- `SuggestionEntry.HistoryItemRef` field stores the `HistoryItem` reference so the renderer can access stats
- `GetHistorySuggestions()` passes `HistoryItem` ref via `SuggestionEntry(string, string, int, HistoryItem)` constructor
- **CRITICAL ΓÇö C# 9 readonly struct field bug**: `SuggestionEntry` is a `struct` and `LangVersion` is 9.0. In C# 9, assigning a `readonly` field in a constructor body after `: this(...)` chaining **silently doesn't take effect** ΓÇö the chained constructor's value wins. The `HistoryItemRef` constructor MUST set all fields directly (no constructor chaining). This was the root cause of tooltips falling through to the generic renderer.
- **Icon styling**: Icons (Γƒ│, ΓÅ▒, ≡ƒôé) use dim-only (`\x1b[2m`), NOT dim+italic ΓÇö italic causes emoji to lean/slant in terminals. Labels and separators use dim+italic (`\x1b[2;3m`). Values use the highlight/accent color.
  - **VT attribute pitfall**: `_listPredictionTooltipColor` defaults to `\x1b[97;2;3m` (bright white + dim + italic). VT SGR attributes are **additive** ΓÇö appending `\x1b[2m` does NOT cancel the italic from the tooltip color. Must use `\x1b[0m\x1b[2m` (full reset, then dim-only) before each icon character.
- Uses existing `ShowToolTips` infrastructure (defaults to `true`)
- In text history mode, tooltips remain `null` (no stats available)

**ExecutionCount is Total Across All Locations** (April 2026):
- Schema stores one `ExecutionHistory` row per command+location pair, each with its own `ExecutionCount`
- `HistoryItem.ExecutionCount` represents the **total** (SUM) across all locations ΓÇö answers "how much do I use this command?"
- `ReadSQLiteHistory`: Uses `TotalCounts` CTE with `SUM(eh.ExecutionCount) GROUP BY CommandLine`, joined to the dedup query
- `ReadHistorySQLiteIncrementally`: Correlated subquery `SELECT SUM(eh2.ExecutionCount) ... WHERE c2.CommandLine = hv.CommandLine`
- `WriteHistoryToSQLite` read-back: `SELECT SUM(ExecutionCount) FROM ExecutionHistory WHERE CommandId = @CommandId` (no LocationId filter)
- **Rationale**: Position in the F2 list already communicates local relevance. "Runs" should have one consistent meaning everywhere.

**Per-Location Execution Counts for Local Sorting** (April 2026):
- `GetLocationExecutionCounts(string location)`: Queries DB for per-location counts via `ExecutionHistory` joined to `Commands` and `Locations`, opens read-only connection. Returns `Dictionary<string, long>` mapping CommandLine → local ExecutionCount.
- Used by `LocationHistoryRecall` and F2 local partition sorting. Global partition and tooltip display still use total `ExecutionCount`.
- **Rationale**: A command run once here but many times elsewhere (e.g., `code .`) shouldn't rank above commands frequently used in this directory.

**F2 List Ordering for SQLite Mode** (April 2026 — implemented):
- **Top half**: Commands matching current directory, sorted by **per-location** frecency (local ExecutionCount + recency)
- **Bottom half**: Global commands (all locations), sorted by **total** frecency, excluding items already in top half
- **Backfill**: If fewer than half-capacity local matches, remaining slots filled from global results
- **Edge cases**: 0 local matches ΓåÆ all global. <5 global after dedup ΓåÆ show fewer total items.
- **Plugins active** (3 history slots): Too few to split ΓÇö just use frecency without partitioning
- **Text mode**: Unchanged ΓÇö pure recency like today
- **Known issue**: Alt+Up/Down (`LocationHistoryRecall`) collides with VS Code integrated terminal ΓÇö VS Code intercepts Alt+Up for terminal selection mode. Works fine in Windows Terminal, iTerm2, standalone pwsh. Consider adding secondary binding (e.g., Ctrl+Alt+Up) or documenting the collision.

**Accessibility Considerations** (April 2026):
- Terminal has no `aria-label` equivalent ΓÇö screen readers read Unicode character names directly from the buffer
- Icons alone (Γƒ│, ΓÅ▒, ≡ƒôé) would confuse screen readers ("clockwise gapped circle arrow 47")
- Decision: Icon + short text label ΓÇö icon for visual scanability, label for screen reader clarity
- Example: `Γƒ│ Runs 47` reads as "runs 47" with the icon as harmless noise

**ReadLine Main Loop Counter Requirement**:
- The main loop in `ReadLine.cs` (~lines 540-650) saves command counters before each key press; if counters didn't change, it resets state (e.g., `_currentHistoryIndex = _history.Count`)
- Any history-related key handler MUST increment `_recallHistoryCommandCount` and/or `_anyHistoryCommandCount` to prevent this reset

**Note**: With .NET 8.0, native library resolution is handled by `NativeLibrary.SetDllImportResolver` - no P/Invoke fallbacks needed.

### 3. Options.cs
**Purpose**: Configuration and settings management

**Key Methods**:
- `SetOptionsInternal()`: Handles `HistoryType` switching between `Text` and `SQLite`
- Lines 29-49: History type initialization ΓÇö switches to SQLite, initializes DB, migrates text history
- Lines 158-185: `HistorySavePathText` / `HistorySavePathSQLite` update handling

**HistoryType Switching**:
```csharp
if (options._historyTypeSpecified)
{
    Options.HistoryType = options.HistoryType;
    if (Options.HistoryType is HistoryType.SQLite)
    {
        // HistorySavePath is now computed from HistoryType, so it already
        // points at HistorySavePathSQLite after the type switch above.
        if (!string.IsNullOrEmpty(Options.HistorySavePath) && !System.IO.File.Exists(Options.HistorySavePath))
        {
            _historyFileMutex?.Dispose();
            _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());
            InitializeSQLiteDatabase(migrateTextHistory: true);
            _historyFileLastSavedSize = 0;
        }
        // Clear text history from memory and load SQLite history
        _singleton._history?.Clear();
        _singleton._currentHistoryIndex = 0;
        ReadSQLiteHistory(fromOtherSession: false);
    }
}
```

**Path Update Handling**:
```csharp
// When user sets -HistorySavePathText
if (options.HistorySavePathText != null)
{
    Options.HistorySavePathText = options.HistorySavePathText;
    // If currently in Text mode, reset the mutex for the new active path.
    if (Options.HistoryType is HistoryType.Text)
    {
        _historyFileMutex?.Dispose();
        _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());
        _historyFileLastSavedSize = 0;
    }
}

// When user sets -HistorySavePathSQLite
if (options.HistorySavePathSQLite != null)
{
    Options.HistorySavePathSQLite = options.HistorySavePathSQLite;
    // If currently in SQLite mode, reconnect to the new database.
    if (Options.HistoryType is HistoryType.SQLite)
    {
        _historyFileMutex?.Dispose();
        _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());
        _historyFileLastSavedSize = 0;
        if (!System.IO.File.Exists(Options.HistorySavePath))
        {
            InitializeSQLiteDatabase();
        }
        _singleton._history?.Clear();
        _singleton._currentHistoryIndex = 0;
        ReadSQLiteHistory(fromOtherSession: false);
    }
}
```

### 4. Cmdlets.cs
**Purpose**: Public API enumerations, cmdlet definitions, and history path configuration

**Key Enumerations**:
```csharp
public enum HistoryType
{
    Text,    // Traditional text-based history (default)
    SQLite   // New SQLite-based history
}

public enum AddToHistoryOption
{
    SkipAdding,
    MemoryOnly,
    MemoryAndFile,
    SQLite
}
```

**History Path Properties** (`PSConsoleReadLineOptions`):
```csharp
/// <summary>
/// The path to the text history file.
/// </summary>
public string HistorySavePathText { get; set; }

/// <summary>
/// The path to the SQLite history database.
/// </summary>
public string HistorySavePathSQLite { get; set; }

/// <summary>
/// Returns the active history save path based on the current HistoryType.
/// </summary>
public string HistorySavePath => HistoryType switch
{
    HistoryType.SQLite => HistorySavePathSQLite,
    _ => HistorySavePathText,
};
```

**Design**: `HistorySavePath` is a computed read-only property. All internal code uses `HistorySavePath` transparently ΓÇö it automatically resolves to the correct stored path based on `HistoryType`. Users configure each path independently via `HistorySavePathText` and `HistorySavePathSQLite`.

**Cmdlet Parameters** (`SetPSReadLineOption`):
```csharp
[Parameter]
[ValidateNotNullOrEmpty]
public string HistorySavePathText { get; set; }  // Sets the text file location

[Parameter]
[ValidateNotNullOrEmpty]
public string HistorySavePathSQLite { get; set; }  // Sets the SQLite DB location
```

**Default Paths** (set during initialization for all platforms):
- **Windows**: `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\{host}_history.txt` / `.db`
- **Linux/macOS (XDG)**: `$XDG_DATA_HOME/powershell/PSReadLine/{host}_history.txt` / `.db`
- **Linux/macOS (HOME)**: `~/.local/share/powershell/PSReadLine/{host}_history.txt` / `.db`
- **Fallback**: `/dev/null` for both

**Usage**:
```powershell
# Switch to SQLite
Set-PSReadLineOption -HistoryType SQLite

# Customize paths independently
Set-PSReadLineOption -HistorySavePathText "C:\MyHistory\history.txt"
Set-PSReadLineOption -HistorySavePathSQLite "C:\MyHistory\history.db"

# Check active path (computed from HistoryType)
(Get-PSReadLineOption).HistorySavePath

# Check both stored paths
(Get-PSReadLineOption).HistorySavePathText
(Get-PSReadLineOption).HistorySavePathSQLite
```

---

## Implementation Checklist

### 1. Framework Status Γ£à COMPLETE
- [x] Changed to `<TargetFramework>net8.0</TargetFramework>`
- [x] Updated `PSReadLine.psd1`: `PowerShellVersion = '7.4'`
- [x] Updated SQLite packages to version 2.1.11+
- [x] Runtime assets auto-deploy (no manual Content configuration)

### 2. Code Status Γ£à COMPLETE
- [x] Using `NativeLibrary.SetDllImportResolver` for native library resolution
- [x] No P/Invoke declarations needed
- [x] Using `RuntimeInformation.RuntimeIdentifier` for platform detection
- [x] Single target framework - no conditional compilation

### 3. History Recall Ordering Γ£à COMPLETE (April 2026)
- [x] `ReadSQLiteHistory`: Chronological + deduplicated SQL (`ROW_NUMBER` window function)
- [x] `HistoryRecall` (Up/Down): Walks `_history` backward, skips `FromOtherSession`, dedup via `HistoryNoDuplicates`
- [x] `LocationHistoryRecall` (Alt+Up/Down): Pre-sorted weighted index (per-location frequency DESC, recency DESC), filtered by current location
- [x] New fields: `_locationSortedIndices`, `_locationSortedPosition` ΓÇö reset when `_locationHistoryCommandCount` resets
- [x] Migrated "Unknown" location entries from text history are invisible to location recall (by design)
- [x] `RemoveFromHistory` (Alt+Delete): Default binding in Windows & Emacs modes ΓÇö removes currently recalled history item from memory and SQLite
- [x] F2 list view: History stats tooltip (Runs/Last/Dir) shown when selecting history items in SQLite mode

### 4. Testing
- [ ] Build and test on Windows/Linux/macOS
- [ ] Test with PowerShell 7.4+ LTS
- [ ] Verify native library loading on all platforms
- [ ] Test history migration from text files
- [ ] Validate SQLite database operations (CRUD, concurrent access)

### 5. Documentation
- [ ] Update README: minimum PowerShell 7.4+ requirement
- [ ] Add release notes: breaking change for PS <7.4 users
- [ ] Document SQLite feature usage and migration path

---

## Build Commands

```powershell
# Build and publish (build script handles native DLL restructuring)
./build.ps1

# Clean build ΓÇö required when incremental builds don't refresh DLLs
./build.ps1 -Clean
./build.ps1

# Or build manually (runtimes/ stays in NuGet layout ΓÇö no restructuring)
dotnet build PSReadLine/PSReadLine.csproj -c Debug
dotnet publish PSReadLine/PSReadLine.csproj -c Debug
```

**IMPORTANT ΓÇö Stale DLL pitfall**: Incremental `./build.ps1` may NOT refresh the published DLLs if the build system considers them up-to-date. If code changes aren't taking effect, run `./build.ps1 -Clean` first, then rebuild. This is especially critical after adding new methods or changing method signatures ΓÇö the old DLL in the `publish/` output (and the installed module folder) won't have the new code.

**Note**: `dotnet build`/`publish` produces the NuGet `runtimes/{rid}/native/` layout. The `BuildMainModule` task in `PSReadLine.build.ps1` restructures this into the flat `{rid}/` layout after publish. If building manually, the native DLLs will be in `runtimes/` and won't be found by PowerShell's ALC.

---

## Troubleshooting

**Code changes not taking effect after rebuild**:
- Incremental `./build.ps1` can leave stale DLLs in `publish/` and the installed module folder
- Fix: `./build.ps1 -Clean` then `./build.ps1` to force a full rebuild
- PowerShell caches assemblies in-process — **all pwsh sessions must be restarted** after deploying a new DLL
- Verify with reflection: check that expected methods exist on the loaded type (see Verification snippet at end of guide)

**Native DLL not found**:
- PSReadLine's `deps.json` is NOT read by the .NET host ΓÇö only `pwsh.deps.json` is processed at startup
- Native library resolution for modules happens via PowerShell's `CorePsAssemblyLoadContext.NativeDllHandler`
- This handler probes `{moduleDir}/{rid}/{libraryName}` (flat layout), NOT `runtimes/{rid}/native/`
- Verify native libraries exist in flat `{rid}/` folders: e.g., `bin/Debug/net8.0/win-x64/e_sqlite3.dll`
- If `runtimes/` exists but `win-x64/` etc. don't, the build script restructuring didn't run
- Use `(Get-Process -Id $PID).Modules | Where-Object ModuleName -match 'sqlite'` to see what actually loaded
- A random `e_sqlite3.dll` on PATH (from other tools) can mask the real problem

**How native resolution actually works for modules**:
1. `COREHOST_TRACE` only shows `pwsh.deps.json` processing ΓÇö module deps.json is never read at the native host layer
2. PowerShell's `CorePsAssemblyLoadContext.NativeDllHandler` resolves native DLLs for modules
3. It builds: `Path.Combine(assemblyDir, runtimeIdentifier, libraryName) + extension`
4. e.g., `{moduleDir}/win-x64/e_sqlite3.dll`
5. If that fails, the OS default search (PATH, system dirs) is tried as a last resort

**deps.json runtimeTargets are dead data for modules**:
- PSReadLine's `deps.json` correctly lists `runtimes/win-x64/native/e_sqlite3.dll` in `runtimeTargets`
- But this is ONLY used when the app's `deps.json` is read (i.e., for standalone apps)
- For PowerShell modules, this data is never consumed ΓÇö confirmed via COREHOST_TRACE analysis

**Multiple SQLite versions**: 
- All SQLitePCLRaw packages must use the same version (currently 2.1.11)
- Check `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3` versions match

**Linux/macOS library loading**: 
- Ensure correct library name prefix: `libe_sqlite3.so` (Linux), `libe_sqlite3.dylib` (macOS)
- Windows uses `e_sqlite3.dll`
- Alpine Linux uses `linux-musl-x64` or `linux-musl-arm64` RIDs

**winsqlite3 fallback**:
- If `SQLitePCLRaw.provider.winsqlite3.dll` is in the build output, SQLitePCLRaw may use Windows' built-in `C:\WINDOWS\SYSTEM32\winsqlite3.DLL` instead of `e_sqlite3`
- This works on Windows but is not the intended provider ΓÇö verify with `Get-Process` modules output

---

## Performance Considerations

### SQLite vs Text History

**Text History**:
- Γ£à Simple, no dependencies
- Γ£à Human-readable
- Γ¥î Linear search O(n)
- Γ¥î Duplicates waste space
- Γ¥î No location/time-based queries

**SQLite History**:
- Γ£à Fast indexed queries O(log n)
- Γ£à Deduplication saves space
- Γ£à Rich query capabilities (location, frequency, time-range)
- Γ£à Transactional integrity
- Γ¥î Requires native library deployment
- Γ¥î Binary format

### Optimization Tips

1. **Use prepared statements** for repeated queries
2. **Index key columns**: `CommandHash`, `LastExecuted`, `ExecutionCount`
3. **Batch inserts** in transactions for better performance
4. **WAL mode** for concurrent read/write: `PRAGMA journal_mode=WAL;`
5. **Lazy initialization**: Only open database when history is accessed

---

## Testing Strategy

### Unit Tests Required

1. **Database initialization**: Schema creation, migration
2. **CRUD operations**: Insert, query, update history entries
3. **Native library loading**: Platform-specific resolution
4. **Error handling**: Corruption, locking, permission issues
5. **Migration**: Text to SQLite conversion accuracy

### Integration Tests Required

1. **Cross-platform**: Windows, Linux, macOS native library loading
2. **PowerShell versions**: 7.0, 7.2, 7.4 compatibility
3. **Concurrent access**: Multiple PowerShell sessions
4. **Large datasets**: Performance with 10k+ history entries
5. **Upgrade scenarios**: netstandard2.0 ΓåÆ net6.0 module replacement

### Test Environments

```yaml
Platform Matrix:
  - Windows 10/11 (x64, ARM64)
  - Ubuntu 20.04/22.04 (x64, ARM64)
  - macOS 12+ (x64, ARM64)

PowerShell Versions:
  - 7.0.x (minimum)
  - 7.2.x (LTS)
  - 7.4.x (LTS)
  - 5.1 (Windows, netstandard2.0 fallback)
```

---

## Module Distribution

### Native Library Resolution Architecture

PowerShell modules with native dependencies face a unique challenge: the module's `deps.json` is **not read** by the .NET native host layer. Only `pwsh.deps.json` is processed during startup. Native library resolution for modules happens entirely through PowerShell's `CorePsAssemblyLoadContext.NativeDllHandler`, which expects a **flat `{rid}/` layout**:

```csharp
// From CorePsAssemblyLoadContext.cs in PowerShell
internal static IntPtr NativeDllHandler(Assembly assembly, string libraryName)
{
    string folder = Path.GetDirectoryName(assembly.Location);
    string fullName = Path.Combine(folder, s_nativeDllSubFolder, libraryName) + s_nativeDllExtension;
    return NativeLibrary.TryLoad(fullName, out IntPtr pointer) ? pointer : IntPtr.Zero;
}
```

This means:
- NuGet convention (`runtimes/{rid}/native/`) does NOT work for PowerShell modules
- PowerShell convention (`{rid}/{libraryName}`) is required
- The build script must restructure the output after `dotnet publish`

### Build Output Structure

After `dotnet publish`, the build script (`PSReadLine.build.ps1`) restructures:
```
runtimes/win-x64/native/e_sqlite3.dll  ΓåÆ  win-x64/e_sqlite3.dll
runtimes/linux-x64/native/libe_sqlite3.so  ΓåÆ  linux-x64/libe_sqlite3.so
```

Final module layout:
```
PSReadLine/
Γö£ΓöÇΓöÇ Microsoft.PowerShell.PSReadLine.dll
Γö£ΓöÇΓöÇ Microsoft.PowerShell.Pager.dll
Γö£ΓöÇΓöÇ Microsoft.Data.Sqlite.dll
Γö£ΓöÇΓöÇ SQLitePCLRaw.core.dll
Γö£ΓöÇΓöÇ SQLitePCLRaw.batteries_v2.dll
Γö£ΓöÇΓöÇ SQLitePCLRaw.provider.e_sqlite3.dll
Γö£ΓöÇΓöÇ PSReadLine.psd1  (PowerShellVersion = '7.4')
Γö£ΓöÇΓöÇ PSReadLine.psm1
Γö£ΓöÇΓöÇ PSReadLine.format.ps1xml
Γö£ΓöÇΓöÇ win-x64/
Γöé   ΓööΓöÇΓöÇ e_sqlite3.dll
Γö£ΓöÇΓöÇ win-arm64/
Γöé   ΓööΓöÇΓöÇ e_sqlite3.dll
Γö£ΓöÇΓöÇ linux-x64/
Γöé   ΓööΓöÇΓöÇ libe_sqlite3.so
Γö£ΓöÇΓöÇ linux-arm64/
Γöé   ΓööΓöÇΓöÇ libe_sqlite3.so
Γö£ΓöÇΓöÇ linux-musl-x64/
Γöé   ΓööΓöÇΓöÇ libe_sqlite3.so
Γö£ΓöÇΓöÇ linux-musl-arm64/
Γöé   ΓööΓöÇΓöÇ libe_sqlite3.so
Γö£ΓöÇΓöÇ osx-x64/
Γöé   ΓööΓöÇΓöÇ libe_sqlite3.dylib
ΓööΓöÇΓöÇ osx-arm64/
    ΓööΓöÇΓöÇ libe_sqlite3.dylib
```

### Supported RIDs

| Platform | RID | PowerShell ships on |
|----------|-----|:---:|
| Windows x64 | `win-x64` | Yes |
| Windows ARM64 | `win-arm64` | Yes |
| Linux x64 | `linux-x64` | Yes |
| Linux ARM | `linux-arm` | Yes |
| Linux ARM64 | `linux-arm64` | Yes |
| Alpine x64 | `linux-musl-x64` | Yes |
| Alpine ARM64 | `linux-musl-arm64` | Yes |
| macOS x64 | `osx-x64` | Yes |
| macOS ARM64 | `osx-arm64` | Yes |

### Future Improvements

Three complementary approaches to fix native library resolution for the PowerShell ecosystem:

1. **PowerShell engine fix** (recommended): Add `runtimes/{rid}/native/` as a fallback probe path in `NativeDllHandler`. ~5 line change, benefits all modules.
2. **PSResourceGet restructuring**: Handle `runtimes/` ΓåÆ `{rid}/` layout transformation during `Install-PSResource`. Benefits all installed modules.
3. **Module-shipped resolver**: PSReadLine can ship its own `NativeLibrary.SetDllImportResolver` as belt-and-suspenders for older PowerShell versions.

**Breaking Change**: PSReadLine 3.0 requires PowerShell 7.4+ (LTS). Users on older versions must stay on PSReadLine 2.x.

---

## Contact & Resources

### Internal Documentation
- `History.cs`: Database implementation, migration, incremental read/write, recall logic
  - `InitializeSQLiteDatabase(bool migrateTextHistory)`: Schema creation (lines ~224-320)
  - `MigrateTextHistoryToSQLite()`: Reads from `HistorySavePathText` directly (lines ~321-420)
  - `WriteHistoryToSQLite()`: Incremental writes with normalized tables (lines ~571-640)
  - `ReadSQLiteHistory()`: Full history load, chronological + deduped SQL (lines ~907-990)
  - `ReadHistorySQLiteIncrementally()`: Cross-session incremental reads (lines ~807-880)
  - `HistoryRecall()`: Basic Up/Down recall, skips `FromOtherSession` (lines ~1538-1600)
  - `LocationHistoryRecall()`: Alt+Up/Down, weighted sorted index using per-location counts (lines ~1738-1820)
  - `GetLocationExecutionCounts(string)`: Queries per-location execution counts from DB (lines ~1497-1535)
  - Fields: `_locationSortedIndices`, `_locationSortedPosition` (lines ~115-116)
  - `RemoveFromHistory()`: Alt+Delete handler ΓÇö removes displayed item from memory + SQLite, advances to next older item (lines ~1612-1680)
    - Must increment `_recallHistoryCommandCount` and `_anyHistoryCommandCount` to prevent main loop index reset
- `KeyBindings.cs`: Key binding dispatch tables
  - Alt+Delete ΓåÆ `RemoveFromHistory` (Windows and Emacs modes)
  - Previously was `KillWord`; `KillWord` remains on Alt+D and Ctrl+Delete (Windows)
- `Prediction.Views.cs`: F2 list view, history stats tooltip
  - `FormatHistoryStatsTooltip(HistoryItem)`: Generates plain-text tooltip (non-null trigger for rendering)
  - `RenderHistoryStatsTooltip(HistoryItem)`: Custom colored renderer ΓÇö icons in dim-only style (no italic to prevent emoji slanting), labels in dim+italic, values in accent color
  - `GetHistorySuggestions()`: Passes `HistoryItem` ref to `SuggestionEntry` for history items when in SQLite mode
  - `LocalFrecencyCompare`: Non-static comparator that uses per-location counts (via `GetLocationExecutionCounts`) for local partition sorting; `FrecencyCompare` (static, total counts) used for global partition
  - Rendering call site: checks `entry.HistoryItemRef != null` ΓåÆ uses `RenderHistoryStatsTooltip`, else generic `RenderTooltip`
- `Prediction.Entry.cs`: Suggestion entry data
  - `SuggestionEntry(string, string, int, HistoryItem)`: Constructor overload for history items with tooltip + item ref
  - `HistoryItemRef` field: stores `HistoryItem` reference for custom tooltip rendering
  - **WARNING**: Must NOT use `: this(...)` constructor chaining ΓÇö C# 9 / `LangVersion 9.0` silently ignores `readonly` field assignments after chain. Set all fields directly.
- `Options.cs`: Configuration management
  - `SetOptionsInternal()`: HistoryType switching (lines 29-49), path updates (lines 158-185)
- `Cmdlets.cs`: Public API definitions
  - `PSConsoleReadLineOptions`: `HistorySavePathText`, `HistorySavePathSQLite`, computed `HistorySavePath` (lines 407-425)
  - `SetPSReadLineOption`: `-HistorySavePathText`, `-HistorySavePathSQLite` parameters (lines 850-873)
  - Default path initialization for all platforms (lines 240-310)
- `ReadLine.cs`: Main loop, field resets
  - Main loop counter-based state reset (lines ~540-650): any history handler must increment its counter or state gets reset
  - `_locationSortedIndices`/`_locationSortedPosition` reset (lines ~626, ~830)
- `PSReadLine.csproj`: Build configuration
- `test/SQLiteHistoryTest.cs`: SQLite-specific tests (~50 tests)
  - Location recall tests: `MultipleItemsSameLocation`, `CaseInsensitivePaths`, `DifferentLocationsFiltered`, `NoLocationFallsBackToNormalRecall`
  - Frequency tests: `FrequentCommandRanksHigher`, `ExecutionCountStoredOnHistoryItem`, `WeightedOrderPreservesChronologyForSingleUse`
  - Migration timestamp tests: `MigrationTimestampsAreChronologicalAndOlderThanNow`, `MigratedTextHistoryOlderThanNewSQLiteEntries`, `UpArrowShowsNewestFirstAfterMigration`
- `test/KillYankTest.cs`: Alt+Delete behavior tests
  - `AltDeleteBoundToRemoveFromHistory_Emacs`, `AltDeleteBoundToRemoveFromHistory_Windows`
  - `AltDeleteAdvancesToNextOlderItem`, `AltDeleteConsecutiveDeletes`, `AltDeleteLastRemainingItem`, `AltDeleteOldestItem`

### External Resources
- [Microsoft.Data.Sqlite Documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
- [SQLitePCLRaw GitHub](https://github.com/ericsink/SQLitePCL.raw)
- [.NET Native Library Loading](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [PowerShell Module Development](https://learn.microsoft.com/en-us/powershell/scripting/developer/module/writing-a-windows-powershell-module)

---

## Summary

**Current Status**: PSReadLine is built on .NET 8.0 with full SQLite support.

**Why .NET 8.0 was required**:
1. netstandard2.0 doesn't populate `runtimeTargets` in deps.json ΓåÆ native DLLs not deployed
2. netstandard2.0 lacks `NativeLibrary.SetDllImportResolver` ΓåÆ can't customize library search
3. .NET 8.0 aligns with PowerShell 7.4 LTS lifecycle

**Key findings from native library investigation (March 2026)**:
- Module `deps.json` `runtimeTargets` are **not consumed** by the .NET host for PowerShell modules (confirmed via `COREHOST_TRACE`)
- Only `pwsh.deps.json` is read at startup; module loading is handled by `CorePsAssemblyLoadContext`
- PowerShell's `NativeDllHandler` expects flat `{rid}/{libraryName}` layout, not NuGet's `runtimes/{rid}/native/`
- Build script restructures NuGet output into PowerShell-compatible layout after `dotnet publish`
- The NuGet `runtimes/` convention has existed since 2016; PowerShell's flat convention since 2018 ΓÇö they were never aligned

**Key decisions from history recall investigation (April 2026)**:
- **Problem**: Same command stored multiple times in `ExecutionHistory` (once per location) ΓÇö caused duplicates in recall. Migrated entries with `Location = "Unknown"` were invisible to location recall.
- **Up/Down Arrow** (`HistoryRecall`): Pure chronological, DB-side dedup via `ROW_NUMBER()`. In-memory dedup governed by `HistoryNoDuplicates` option.
- **Alt+Up/Down Arrow** (`LocationHistoryRecall`): Builds pre-sorted weighted index on first press — **per-location** frequency DESC (via `GetLocationExecutionCounts`), then recency DESC. Only includes commands matching current `$PWD`. "Unknown" location entries are excluded by design.
- **Alt+Delete** (`RemoveFromHistory`): Default binding in Windows & Emacs modes. Removes the currently recalled history entry from in-memory history and SQLite. Advances to next older item after deletion. Also works in F2 list view. Previously bound to `KillWord` (still available via `Alt+D` / `Ctrl+Delete`). Must increment `_recallHistoryCommandCount`/`_anyHistoryCommandCount` to prevent main loop reset.
- **F2 List View Stats Tooltip**: Custom colored renderer in SQLite mode. Format: `Γ¥»Γ¥» Γƒ│ Runs N  Γöé  ΓÅ▒ Last Xh ago  Γöé  ≡ƒôé Dir path`. Icons + short labels for accessibility. Cannot embed VT sequences in tooltip text (char-by-char renderer treats `\x1b` as control char), so `RenderHistoryStatsTooltip` writes directly to buffer. `HistoryItemRef` on `SuggestionEntry` provides the data. Icons use dim-only style (italic makes emoji lean). Labels use dim+italic. Values use accent color.
  - **C# 9 gotcha**: `SuggestionEntry` is a struct with `LangVersion 9.0`. `readonly` fields assigned in constructor body after `: this(...)` chaining silently don't take effect. The `HistoryItemRef` constructor must set all fields directly ΓÇö no chaining.
- **ExecutionCount = Total (SUM) for display**: `ExecutionCount` on `HistoryItem` is always the sum across all locations. All three SQL read paths (`ReadSQLiteHistory`, `ReadHistorySQLiteIncrementally`, `WriteHistoryToSQLite` read-back) use `SUM(ExecutionCount)`. Tooltip "Runs N" shows this total. **But local sorting uses per-location counts** via `GetLocationExecutionCounts()` — a command run once here but 100× elsewhere shouldn't dominate local rankings.
- **F2 List Ordering** (implemented): Top half = current-dir commands by per-location frecency, bottom half = global by total frecency, with backfill. Plugins-active (3 slots) = just total frecency, no split.
- **Alt+Up VS Code Collision**: VS Code intercepts Alt+Up in integrated terminal for selection mode ΓÇö `LocationHistoryRecall` is unreachable. Works in Windows Terminal/iTerm2/standalone pwsh. May need secondary binding or documentation.
- **Timestamps**: Stored as Unix seconds (`ToUnixTimeSeconds()`), read with `FromUnixTimeSeconds()`. Do NOT use .NET ticks conversion in ad-hoc SQL queries.
- **Migration timestamps**: Assigned post-collection with oldest text line = earliest time, all before UtcNow. Items loaded from DB must have `_saved = true` to prevent re-write with UtcNow.

**Developer Note**: Always use `./build.ps1` to get the correct module layout. Manual `dotnet build` output will have native DLLs in `runtimes/` which PowerShell cannot find. If changes aren't taking effect after rebuild, use `./build.ps1 -Clean` then rebuild ΓÇö incremental builds can leave stale DLLs in `publish/` and the installed module folder. Also, PowerShell caches loaded assemblies in-process ΓÇö you must **restart all pwsh sessions** after deploying a new DLL.

**Verification**: After loading a new build, confirm the expected methods exist:
```powershell
# Example: verify GetLocationExecutionCounts is present
[Microsoft.PowerShell.PSConsoleReadLine].GetMethods(
    [System.Reflection.BindingFlags]'NonPublic, Instance'
) | Where-Object { $_.Name -match 'Location' } | ForEach-Object { "$($_.Name)($($_.GetParameters().Count) params)" }
```

---

**End of Guide**
