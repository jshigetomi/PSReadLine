# API Surface & Code Map

## Public Enumerations

### HistoryType (Cmdlets.cs)

```csharp
public enum HistoryType
{
    Text,    // Traditional text-based history (default)
    SQLite   // New SQLite-based history
}
```

### AddToHistoryOption (Cmdlets.cs)

```csharp
public enum AddToHistoryOption
{
    SkipAdding,
    MemoryOnly,
    MemoryAndFile,
    SQLite
}
```

---

## PSConsoleReadLineOptions (Cmdlets.cs)

### History Path Properties

```csharp
/// <summary>The path to the text history file.</summary>
public string HistorySavePathText { get; set; }

/// <summary>The path to the SQLite history database.</summary>
public string HistorySavePathSQLite { get; set; }

/// <summary>Returns the active history save path based on the current HistoryType.</summary>
public string HistorySavePath => HistoryType switch
{
    HistoryType.SQLite => HistorySavePathSQLite,
    _ => HistorySavePathText,
};
```

**Design**: `HistorySavePath` is a computed read-only property. All internal code uses `HistorySavePath` transparently — it resolves to the correct stored path based on `HistoryType`. Users configure each path independently.

### Default Paths (set during initialization for all platforms)

- **Windows**: `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\{host}_history.txt` / `.db`
- **Linux/macOS (XDG)**: `$XDG_DATA_HOME/powershell/PSReadLine/{host}_history.txt` / `.db`
- **Linux/macOS (HOME)**: `~/.local/share/powershell/PSReadLine/{host}_history.txt` / `.db`
- **Fallback**: `/dev/null` for both

---

## SetPSReadLineOption Cmdlet Parameters (Cmdlets.cs)

```csharp
[Parameter]
[ValidateNotNullOrEmpty]
public string HistorySavePathText { get; set; }  // Sets the text file location

[Parameter]
[ValidateNotNullOrEmpty]
public string HistorySavePathSQLite { get; set; }  // Sets the SQLite DB location
```

### Usage Examples

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

## Options.cs — HistoryType Switching

### SetOptionsInternal() — Lines 29-49

```csharp
if (options._historyTypeSpecified)
{
    Options.HistoryType = options.HistoryType;
    if (Options.HistoryType is HistoryType.SQLite)
    {
        if (!string.IsNullOrEmpty(Options.HistorySavePath) && !System.IO.File.Exists(Options.HistorySavePath))
        {
            _historyFileMutex?.Dispose();
            _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());
            InitializeSQLiteDatabase(migrateTextHistory: true);
            _historyFileLastSavedSize = 0;
        }
        _singleton._history?.Clear();
        _singleton._currentHistoryIndex = 0;
        ReadSQLiteHistory(fromOtherSession: false);
    }
}
```

### Path Update Handling — Lines 158-185

**HistorySavePathText update**: Resets mutex if currently in Text mode.

**HistorySavePathSQLite update**: If currently in SQLite mode, reconnects — disposes mutex, creates new one, initializes DB if file doesn't exist, clears memory, reloads from new DB.

---

## File-Level Cross-References

### History.cs — Database implementation, migration, recall logic

| Method/Field | Purpose | Approx. Lines |
|---|---|---|
| `InitializeSQLiteDatabase(bool)` | Schema creation | ~224-320 |
| `MigrateTextHistoryToSQLite()` | Reads from `HistorySavePathText` | ~321-420 |
| `WriteHistoryToSQLite()` | Incremental writes, normalized tables | ~571-640 |
| `ReadSQLiteHistory()` | Full load, chronological + deduped SQL | ~907-990 |
| `ReadHistorySQLiteIncrementally()` | Cross-session incremental reads | ~807-880 |
| `HistoryRecall()` | Up/Down recall, skips `FromOtherSession` | ~1538-1600 |
| `LocationHistoryRecall()` | Alt+Up/Down, weighted sorted index | ~1738-1820 |
| `GetLocationExecutionCounts(string)` | Per-location counts from DB | ~1497-1535 |
| `RemoveFromHistory()` | Alt+Delete handler | ~1612-1680 |
| `_locationSortedIndices`, `_locationSortedPosition` | Location recall state | ~115-116 |

### KeyBindings.cs — Key binding dispatch tables

- Alt+Delete → `RemoveFromHistory` (Windows and Emacs modes)
- Previously `KillWord`; `KillWord` remains on Alt+D and Ctrl+Delete (Windows)

### Prediction.Views.cs — F2 list view, history stats tooltip

| Method/Field | Purpose |
|---|---|
| `FormatHistoryStatsTooltip(HistoryItem)` | Plain-text tooltip (non-null trigger) |
| `RenderHistoryStatsTooltip(HistoryItem)` | Custom colored renderer (dim icons, dim+italic labels, accent values) |
| `GetHistorySuggestions()` | Passes `HistoryItem` ref to `SuggestionEntry` in SQLite mode |
| `LocalFrecencyCompare` | Non-static, per-location counts for local partition |
| `FrecencyCompare` | Static, total counts for global partition |

### Prediction.Entry.cs — Suggestion entry data

| Method/Field | Purpose |
|---|---|
| `SuggestionEntry(string, string, int, HistoryItem)` | Constructor overload for history items |
| `HistoryItemRef` | Stores `HistoryItem` for tooltip rendering |
| **WARNING** | Must NOT use `: this(...)` chaining — C# 9 bug |

### Options.cs — Configuration management

| Location | Purpose |
|---|---|
| Lines 29-49 | HistoryType switching |
| Lines 158-185 | Path update handling |

### Cmdlets.cs — Public API definitions

| Location | Purpose |
|---|---|
| Lines 407-425 | `HistorySavePathText`, `HistorySavePathSQLite`, computed `HistorySavePath` |
| Lines 850-873 | `-HistorySavePathText`, `-HistorySavePathSQLite` parameters |
| Lines 240-310 | Default path initialization for all platforms |

### ReadLine.cs — Main loop, field resets

| Location | Purpose |
|---|---|
| Lines ~540-650 | Main loop counter-based state reset |
| Lines ~626, ~830 | `_locationSortedIndices`/`_locationSortedPosition` reset |

### PSReadLine.csproj — Build configuration

`net8.0` target framework, `Microsoft.Data.Sqlite` 9.0.4, `SQLitePCLRaw.bundle_e_sqlite3` 2.1.11.
