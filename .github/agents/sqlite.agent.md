---
name: sqlite
code description: Expert agent for implementing SQLite-based history in PSReadLine, covering .NET framework migration, native library deployment, and cross-platform compatibility.
argument-hint: SQLite implementation tasks, migration questions, or troubleshooting help
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo']
---

# PSReadLine SQLite History Implementation Guide

**Last Updated**: March 3, 2026  
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

### Current Implementation

**PSReadLine now targets .NET 8.0 exclusively**:

```xml
<TargetFramework>net8.0</TargetFramework>  <!-- PowerShell 7.4+ LTS -->
```

### PowerShell/Runtime Compatibility

| PowerShell | .NET Runtime | Support Status | Works with net6.0? | Works with net8.0? |
|-----------|--------------|----------------|-------------------|-------------------|
| 5.1 | .NET Framework 4.7.2 | Indefinite | ❌ No | ❌ No |
| 7.0 | .NET Core 3.1 | Ended Dec 2022 | ✅ Yes | ✅ Yes |
| 7.2 LTS | .NET 6.0 | Ended Nov 2024 | ✅ Yes | ✅ Yes |
| 7.4 LTS | .NET 8.0 | Until Nov 2026 | ✅ Yes | ✅ Yes |

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

**Result**: Native libraries automatically deploy to `bin/Debug/net8.0/runtimes/{rid}/native/` with no manual configuration required. The .NET build system handles all platform-specific native DLL deployment.

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

**Note**: With .NET 8.0, native library resolution is handled by `NativeLibrary.SetDllImportResolver` - no P/Invoke fallbacks needed.

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

### 1. Framework Status ✅ COMPLETE
- [x] Changed to `<TargetFramework>net8.0</TargetFramework>`
- [x] Updated `PSReadLine.psd1`: `PowerShellVersion = '7.4'`
- [x] Updated SQLite packages to version 2.1.11+
- [x] Runtime assets auto-deploy (no manual Content configuration)

### 2. Code Status ✅ COMPLETE
- [x] Using `NativeLibrary.SetDllImportResolver` for native library resolution
- [x] No P/Invoke declarations needed
- [x] Using `RuntimeInformation.RuntimeIdentifier` for platform detection
- [x] Single target framework - no conditional compilation

### 3. Testing
- [ ] Build and test on Windows/Linux/macOS
- [ ] Test with PowerShell 7.4+ LTS
- [ ] Verify native library loading on all platforms
- [ ] Test history migration from text files
- [ ] Validate SQLite database operations (CRUD, concurrent access)

### 4. Documentation
- [ ] Update README: minimum PowerShell 7.4+ requirement
- [ ] Add release notes: breaking change for PS <7.4 users
- [ ] Document SQLite feature usage and migration path

---

## Build Commands

```powershell
# Build (runtimes are automatically included)
dotnet build PSReadLine/PSReadLine.csproj -c Debug
dotnet build PSReadLine/PSReadLine.csproj -c Release

# Or use the PSReadLine build script
./build.ps1

# Publish for specific platform (includes all necessary native libraries)
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r win-x64
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r linux-x64
dotnet publish PSReadLine/PSReadLine.csproj -c Release -r osx-x64
```

**Note**: Native SQLite libraries for all platforms are automatically included in the build output under `runtimes/{rid}/native/`.

---

## Troubleshooting

**Native DLL not found**: 
- Verify native libraries exist in `bin/Debug/net8.0/runtimes/{rid}/native/`
- Check platform identifier: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`
- Libraries auto-deploy with .NET 8.0 build system

**Multiple SQLite versions**: 
- All SQLitePCLRaw packages must use the same version (currently 2.1.11)
- Check `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3` versions match

**Linux/macOS library loading**: 
- Ensure correct library name prefix: `libe_sqlite3.so` (Linux), `libe_sqlite3.dylib` (macOS)
- Windows uses `e_sqlite3.dll`
- Platform detection uses `RuntimeInformation.IsOSPlatform()`

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

Current build output structure (net8.0):
```
PSReadLine/
├── net8.0/
│   ├── Microsoft.PowerShell.PSReadLine.dll
│   ├── Microsoft.PowerShell.Pager.dll
│   ├── Microsoft.Data.Sqlite.dll
│   └── runtimes/  (auto-deployed by build system)
│       ├── win-x64/native/e_sqlite3.dll
│       ├── linux-x64/native/libe_sqlite3.so
│       ├── osx-x64/native/libe_sqlite3.dylib
│       └── osx-arm64/native/libe_sqlite3.dylib
├── PSReadLine.psd1  (PowerShellVersion = '7.4')
├── PSReadLine.psm1
└── PSReadLine.format.ps1xml
```

**Breaking Change**: PSReadLine 3.0 requires PowerShell 7.4+ (LTS). Users on older versions must stay on PSReadLine 2.x.

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

**Current Status**: PSReadLine is built on .NET 8.0 with full SQLite support.

**Why .NET 8.0 was required**:
1. netstandard2.0 doesn't populate `runtimeTargets` in deps.json → native DLLs not deployed
2. netstandard2.0 lacks `NativeLibrary.SetDllImportResolver` → can't customize library search
3. .NET 8.0 aligns with PowerShell 7.4 LTS lifecycle

**Key Benefits**:
- ✅ Automatic native library deployment for all platforms
- ✅ Full `NativeLibrary` API support for custom resolution
- ✅ Simplified build process - no manual runtime configuration
- ✅ Long-term support until November 2026 (PS 7.4 LTS)

**Developer Note**: Native runtimes are automatically included in build output. No manual Content copying or ItemGroup configuration needed in the .csproj file.

---

**End of Guide**