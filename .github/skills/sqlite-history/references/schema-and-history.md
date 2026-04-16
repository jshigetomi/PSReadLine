# Schema & History Logic

## Database Schema

Normalized structure with three tables:

- **Commands**: Stores unique command text with hash for fast lookup
- **Locations**: Stores unique directory paths (normalized)
- **ExecutionHistory**: Junction table — one row per command+location pair, each with its own `ExecutionCount`, `StartTime` (Unix seconds), and `LastExecuted` (Unix seconds)

Foreign keys and indexes on `CommandHash`, `LastExecuted`, `ExecutionCount`.

---

## History Recall Ordering

### Up/Down Arrow (`HistoryRecall`)

`ReadSQLiteHistory()` loads history **chronologically** (`LastExecuted DESC`) with **per-CommandLine deduplication** via `ROW_NUMBER() OVER (PARTITION BY CommandLine ORDER BY LastExecuted DESC)`.

- Walks `_history` backward, skips `FromOtherSession` items
- In-memory dedup governed by `HistoryNoDuplicates` option
- The SQL already deduplicates DB-loaded entries

### Alt+Up/Down Arrow (`LocationHistoryRecall`)

Builds a **weighted sorted index** on first press:
- Filtered to current location (`$PWD`)
- Sorted by **per-location** `ExecutionCount DESC` (frequency at this directory), then position DESC (recency)
- In SQLite mode, queries per-location counts via `GetLocationExecutionCounts()` so that a command run once here but 100 times elsewhere doesn't dominate
- Falls back to total `ExecutionCount` in Text mode
- "Unknown" location entries from text history migration are excluded by design

Fields: `_locationSortedIndices`, `_locationSortedPosition` — reset when `_locationHistoryCommandCount` resets.

**Known issue**: Alt+Up/Down collides with VS Code integrated terminal — VS Code intercepts Alt+Up for terminal selection mode. Works fine in Windows Terminal, iTerm2, standalone pwsh.

### F2 List Ordering for SQLite Mode

- **Top half**: Commands matching current directory, sorted by **per-location** frecency (local ExecutionCount + recency)
- **Bottom half**: Global commands (all locations), sorted by **total** frecency, excluding items already in top half
- **Backfill**: If fewer than half-capacity local matches, remaining slots filled from global results
- **Edge cases**: 0 local matches → all global. <5 global after dedup → show fewer total items
- **Plugins active** (3 history slots): Too few to split — just use frecency without partitioning
- **Text mode**: Unchanged — pure recency like today

---

## ExecutionCount Semantics

Schema stores one `ExecutionHistory` row per command+location pair, each with its own `ExecutionCount`.

`HistoryItem.ExecutionCount` represents the **total** (SUM) across all locations — answers "how much do I use this command?"

### SQL Paths

- `ReadSQLiteHistory`: Uses `TotalCounts` CTE with `SUM(eh.ExecutionCount) GROUP BY CommandLine`, joined to dedup query
- `ReadHistorySQLiteIncrementally`: Correlated subquery `SELECT SUM(eh2.ExecutionCount) ... WHERE c2.CommandLine = hv.CommandLine`
- `WriteHistoryToSQLite` read-back: `SELECT SUM(ExecutionCount) FROM ExecutionHistory WHERE CommandId = @CommandId` (no LocationId filter)

**Rationale**: Position in the F2 list already communicates local relevance. "Runs" should have one consistent meaning everywhere.

### Per-Location Execution Counts

`GetLocationExecutionCounts(string location)`: Queries DB for per-location counts via `ExecutionHistory` joined to `Commands` and `Locations`, opens read-only connection. Returns `Dictionary<string, long>` mapping CommandLine → local ExecutionCount.

Used by `LocationHistoryRecall` and F2 local partition sorting. Global partition and tooltip display still use total `ExecutionCount`.

---

## History Deletion

### Alt+Delete (`RemoveFromHistory`)

Default binding in both Windows and Emacs modes. While browsing history with Up/Down:
- Removes the currently displayed entry from in-memory history and the SQLite database
- After deletion, advances to the next **older** item (same direction as Up arrow) so the user can keep pressing Alt+Delete to delete consecutive items
- Also works in the F2 prediction list view

**Previously**: Alt+Delete was bound to `KillWord`; that function remains available via `Alt+D` and `Ctrl+Delete` (Windows).

**Critical**: `RemoveFromHistory` must increment `_recallHistoryCommandCount` and `_anyHistoryCommandCount` so the ReadLine main loop doesn't reset `_currentHistoryIndex` after the key press.

### Programmatic APIs

- `RemoveHistoryItem(string)`: Removes a specific command by text
- `ClearHistory` (Alt+F7 on Windows): Clears all history

---

## Database Initialization

- `InitializeSQLiteDatabase(bool migrateTextHistory = false)` — creates schema if new DB
- `migrateTextHistory: true` only on initial Text → SQLite switch (not when relocating DB)
- Migration reads from `_options.HistorySavePathText` directly (no path derivation hacks)

---

## Text-to-SQLite Migration

### Timestamp Assignment

Migrated items must all have timestamps **older** than "now" so they sort before any new SQLite entries.

**Correct approach**: Collect items first, then assign timestamps:
```
migrationBase = UtcNow - (Count+1) minutes
each item gets migrationBase.AddMinutes(idx)
```
Oldest text line gets earliest timestamp.

**Do NOT** assign timestamps during collection — the growing count inverts the order.

### `_saved` Flag

`ReadSQLiteHistory` and `ReadHistorySQLiteIncrementally` must set `_saved = true` on all loaded HistoryItems. Without this, `IncrementalHistoryWrite` re-writes loaded items with `LastExecuted = UtcNow`, destroying original timestamps and scrambling order.

---

## F2 List View History Stats Tooltip

When `HistoryType` is `SQLite`, selecting a history item in the F2 prediction list view shows a colored stats tooltip beneath the selected entry.

### Format
```
↻ Runs 47  │  🕒 Last 2m ago  │  📁 Dir ~/repos/PSReadline
```

### Fields
- `ExecutionCount` (total across all locations via SUM)
- `StartTime` (relative time)
- `Location` (if not "Unknown")

### Rendering Architecture

- **Custom renderer** required: `RenderHistoryStatsTooltip(HistoryItem)` writes colored output directly to buffer lines — cannot use generic `RenderTooltip` because it iterates char-by-char and treats VT escape chars as control characters (renders `^[`)
- `FormatHistoryStatsTooltip(HistoryItem)` generates plain-text version used as non-null `ToolTip` trigger
- `SuggestionEntry.HistoryItemRef` field stores the `HistoryItem` reference so the renderer can access stats
- `GetHistorySuggestions()` passes `HistoryItem` ref via `SuggestionEntry(string, string, int, HistoryItem)` constructor
- Rendering call site: checks `entry.HistoryItemRef != null` → uses `RenderHistoryStatsTooltip`, else generic `RenderTooltip`

### Icon Styling

- Icons (↻, 🕒, 📁) use dim-only (`\x1b[2m`), NOT dim+italic — italic causes emoji to lean/slant in terminals
- Labels and separators use dim+italic (`\x1b[2;3m`)
- Values use the highlight/accent color
- **VT pitfall**: `_listPredictionTooltipColor` defaults to `\x1b[97;2;3m` (bright white + dim + italic). VT SGR attributes are **additive** — must use `\x1b[0m\x1b[2m` (full reset, then dim-only) before each icon
- Uses existing `ShowToolTips` infrastructure (defaults to `true`)
- In text history mode, tooltips remain `null` (no stats available)

### C# 9 Struct Bug

`SuggestionEntry` is a `struct` and `LangVersion` is 9.0. In C# 9, assigning a `readonly` field in a constructor body after `: this(...)` chaining **silently doesn't take effect** — the chained constructor's value wins. The `HistoryItemRef` constructor MUST set all fields directly (no constructor chaining). This was the root cause of tooltips falling through to the generic renderer.

### Accessibility

- Terminal has no `aria-label` equivalent — screen readers read Unicode character names directly from the buffer
- Icons alone would confuse screen readers ("clockwise gapped circle arrow 47")
- Decision: Icon + short text label — icon for visual scanability, label for screen reader clarity
- Example: `↻ Runs 47` reads as "runs 47" with the icon as harmless noise

### F2 Sorting Helpers

- `LocalFrecencyCompare`: Non-static comparator that uses per-location counts (via `GetLocationExecutionCounts`) for local partition sorting
- `FrecencyCompare` (static, total counts): Used for global partition

---

## Testing

### SQLite-Specific Tests (~50 tests in `test/SQLiteHistoryTest.cs`)

**Location recall**:
- `MultipleItemsSameLocation`, `CaseInsensitivePaths`, `DifferentLocationsFiltered`, `NoLocationFallsBackToNormalRecall`

**Frequency**:
- `FrequentCommandRanksHigher`, `ExecutionCountStoredOnHistoryItem`, `WeightedOrderPreservesChronologyForSingleUse`

**Migration timestamps**:
- `MigrationTimestampsAreChronologicalAndOlderThanNow`, `MigratedTextHistoryOlderThanNewSQLiteEntries`, `UpArrowShowsNewestFirstAfterMigration`

### Alt+Delete Tests (`test/KillYankTest.cs`)

- `AltDeleteBoundToRemoveFromHistory_Emacs`, `AltDeleteBoundToRemoveFromHistory_Windows`
- `AltDeleteAdvancesToNextOlderItem`, `AltDeleteConsecutiveDeletes`, `AltDeleteLastRemainingItem`, `AltDeleteOldestItem`

### Test Infrastructure Notes

- `_.AltUpArrow` is NOT available in the test keyboard JSON — bind `PreviousLocationHistory` to `UpArrow` in tests instead
- `_.Alt_Delete` IS available for `RemoveFromHistory` tests
- `Test("expected", Keys(...))` expects the line content when Enter is pressed
- After Up+Down navigation returning to the prompt, the saved current line (usually empty) is restored
- Use `CheckThat(() => AssertLineIs("expected"))` between key presses to verify intermediate state
