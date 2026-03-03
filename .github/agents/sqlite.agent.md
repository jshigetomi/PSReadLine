---
name: sqlite
description: Expert agent for implementing SQLite-based history in PSReadLine, covering .NET framework migration, native library deployment, and cross-platform compatibility.
argument-hint: SQLite implementation tasks, migration questions, or troubleshooting help
# tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo'] # specify the tools this agent can use. If not set, all enabled tools are allowed.
---

# PSReadLine SQLite History Implementation Guide

**Last Updated**: December 19, 2025  
**Status**: Framework Migration Required  
**Target**: PowerShell 7.0.0-rc+ compatibility

---

## Executive Summary

This guide documents implementing SQLite-based history for PSReadLine. The core issue: **Microsoft.Data.Sqlite requires .NET 6+ for proper native library deployment**. While the package claims netstandard2.0 compatibility, the build system doesn't properly deploy platform-specific native DLLs, and netstandard2.0 lacks the APIs needed to resolve them at runtime.

**Solution**: Target net6.0 or net8.0 (clean break from netstandard2.0).

---

## Why netstandard2.0 Fails with SQLite

### The Technical Issue

**deps.json Missing Runtime Assets**:

When building for netstandard2.0, the generated `deps.json` contains:
```json
"SQLitePCLRaw.lib.e_sqlite3/2.1.10": {
  "runtimeTargets": {}  // ← EMPTY - no native DLLs listed
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

### Recommended Approach: Single Target

**Drop netstandard2.0 entirely** - target net6.0 or net8.0 only:

```xml
<TargetFramework>net6.0</TargetFramework>  <!-- Minimum: PS 7.2+ -->
<!-- OR -->
<TargetFramework>net8.0</TargetFramework>  <!-- Minimum: PS 7.4+ -->
```

### PowerShell/Runtime Compatibility

| PowerShell | .NET Runtime | Support Status | Works with net6.0? | Works with net8.0? |
|-----------|--------------|----------------|-------------------|-------------------|
| 5.1 | .NET Framework 4.7.2 | Indefinite | ❌ No | ❌ No |
| 7.0 | .NET Core 3.1 | Ended Dec 2022 | ✅ Yes | ✅ Yes |
| 7.2 LTS | .NET 6.0 | Ended Nov 2024 | ✅ Yes | ✅ Yes |
| 7.4 LTS | .NET 8.0 | Until Nov 2026 | ✅ Yes | ✅ Yes |

### Recommendation

**Target net8.0** for simplicity:
- .NET 6 is already out of support (Nov 2024)
- Single build output - easier testing
- PowerShell 7.4 LTS is current recommended version
- Users on older versions can stay on PSReadLine 2.x

**Note**: .NET Standard 2.0 has no end-of-support date (it's a specification, not a runtime), but it lacks the APIs needed for SQLite native library loading.

---

## Implementation Files Overview

### 1. PSReadLine.csproj
**Purpose**: Project configuration and dependency management

**Change Required**:
```xml
<!-- Change from: -->
<TargetFramework>netstandard2.0</TargetFramework>

<!-- To: -->
<TargetFramework>net6.0</TargetFramework>  <!-- or net8.0 -->

<!-- SQLite packages (no conditional logic needed) -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.4" />
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.11" />
```

**Result**: Native libraries automatically deploy to `bin/Debug/net6.0/runtimes/{rid}/native/` - no manual Content copying needed.

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

**Action**: Remove old P/Invoke code (lines 1922-2108) - no longer needed with net6.0+

### 3. Options.cs
**Purpose**: Configuration and settings management

**Key Methods**:
- `SetOptionsInternal()`: Handles `HistoryType` switching between `Text` and `SQLite`
- Lines 31-57: History type initialization logic

**SQLite-Specific Settings**:
```csharp
if (options._historyTypeSpecified)
{
    Options.HistoryType = options.HistoryType;
    if (Options.HistoryType is HistoryType.SQLite)
    {
        InitializeSQLiteDatabase();
        // Migrate existing text history to SQLite
        if (!string.IsNullOrEmpty(Options.HistorySavePath) && 
            !System.IO.File.Exists(Options.HistorySavePath))
        {
            MigrateTextHistoryToSQLite();
        }
    }
}
```

### 4. Cmdlets.cs
**Purpose**: Public API enumerations and cmdlet definitions

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

**Usage**:
```powershell
Set-PSReadLineOption -HistoryType SQLite
```

---

## Implementation Checklist

### 1. Framework Migration
- [ ] Change `<TargetFramework>netstandard2.0</TargetFramework>` to `<TargetFramework>net6.0</TargetFramework>` (or net8.0)
- [ ] Update `PSReadLine.psd1`: `PowerShellVersion = '7.2'` (or '7.4' for net8.0)
- [ ] Update SQLite packages to version 2.1.11+
- [ ] Remove manual Content copying (runtime assets auto-deploy)

### 2. Code Updates
- [ ] Replace native library resolver in History.cs with `NativeLibrary.SetDllImportResolver`
- [ ] Remove P/Invoke declarations (LoadLibrary, dlopen) - no longer needed
- [ ] Simplify `GetSQLiteLibraryPaths()` to use `RuntimeInformation.RuntimeIdentifier`
- [ ] Remove conditional compilation (`#if NETSTANDARD2_0`) - single target now

### 3. Testing
- [ ] Build and test on Windows/Linux/macOS
- [ ] Test with PowerShell 7.2+ (or 7.4+ if using net8.0)
- [ ] Verify native library loading on all platforms
- [ ] Test history migration from text files

### 4. Documentation
- [ ] Update README: minimum PowerShell version requirement
- [ ] Add release notes: breaking change for PS 5.1/7.0/7.1 users
- [ ] Document SQLite feature usage

---

## Build Commands

```powershell
# Build
dotnet build PSReadLine/PSReadLine.csproj

# Publish for specific platform
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r win-x64
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r linux-x64
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r osx-x64
```

No conditional compilation needed with single-target approach.

---

## Troubleshooting

**Native DLL not found**: Verify native libraries exist in `bin/Debug/net6.0/runtimes/{rid}/native/`. Should auto-deploy with net6.0+.

**Multiple SQLite versions**: Pin all SQLitePCLRaw packages to same version (2.1.11) to avoid conflicts.

**Linux/macOS library loading**: Ensure correct library name prefix (`libe_sqlite3.so` not `e_sqlite3.so`).

---

## Performance Considerations

### SQLite vs Text History

**Text History**:
- ✅ Simple, no dependencies
- ✅ Human-readable
- ❌ Linear search O(n)
- ❌ Duplicates waste space
- ❌ No location/time-based queries

**SQLite History**:
- ✅ Fast indexed queries O(log n)
- ✅ Deduplication saves space
- ✅ Rich query capabilities (location, frequency, time-range)
- ✅ Transactional integrity
- ❌ Requires native library deployment
- ❌ Binary format

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
5. **Upgrade scenarios**: netstandard2.0 → net6.0 module replacement

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

With single-target net6.0/net8.0:
```
PSReadLine/
├── net6.0/ (or net8.0/)
│   ├── Microsoft.PowerShell.PSReadLine.dll
│   └── runtimes/
│       ├── win-x64/native/e_sqlite3.dll
│       ├── linux-x64/native/libe_sqlite3.so
│       └── osx-x64/native/libe_sqlite3.dylib
└── PSReadLine.psd1  (PowerShellVersion = '7.2' or '7.4')
```

**Breaking Change**: PSReadLine 3.0 requires PowerShell 7.2+ (net6.0) or 7.4+ (net8.0). Users on PS 5.1/7.0/7.1 must stay on PSReadLine 2.x.

---

## Contact & Resources

### Internal Documentation
- `History.cs`: Database implementation (lines 141-700)
- `Options.cs`: Configuration management (lines 31-57)
- `Cmdlets.cs`: Public API definitions (lines 64-70)
- `PSReadLine.csproj`: Build configuration

### External Resources
- [Microsoft.Data.Sqlite Documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
- [SQLitePCLRaw GitHub](https://github.com/ericsink/SQLitePCL.raw)
- [.NET Native Library Loading](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [PowerShell Module Development](https://learn.microsoft.com/en-us/powershell/scripting/developer/module/writing-a-windows-powershell-module)

---

## Summary

SQLite requires **net6.0 or net8.0** (not netstandard2.0) because:
1. netstandard2.0 doesn't populate `runtimeTargets` in deps.json → native DLLs not deployed
2. netstandard2.0 lacks `NativeLibrary.SetDllImportResolver` → can't customize library search

**Solution**: Single-target net6.0 or net8.0 with clean break from older PowerShell versions.

**Implementation**: 3-5 days (framework change, code cleanup, testing)

---

**End of Guide**