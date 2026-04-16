# Architecture & Native Libraries

## Why netstandard2.0 Fails with SQLite

### deps.json Missing Runtime Assets

When building for netstandard2.0, the generated `deps.json` contains:
```json
"SQLitePCLRaw.lib.e_sqlite3/2.1.10": {
  "runtimeTargets": {}  // EMPTY - no native DLLs listed
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
- Manual copying doesn't help — runtime still doesn't know about the files

**Missing APIs**:
- netstandard2.0 lacks `NativeLibrary.SetDllImportResolver` (requires .NET 5+)
- No way to customize native library search paths
- P/Invoke fallbacks (LoadLibrary/dlopen) don't register with .NET's import system

---

## Framework Migration

### Current Implementation

```xml
<TargetFramework>net8.0</TargetFramework>  <!-- PowerShell 7.4+ LTS -->
```

### PowerShell/Runtime Compatibility

| PowerShell | .NET Runtime | Support Status | Works with net8.0? |
|-----------|--------------|----------------|-------------------|
| 5.1 | .NET Framework 4.7.2 | Indefinite | No |
| 7.0 | .NET Core 3.1 | Ended Dec 2022 | Yes |
| 7.2 LTS | .NET 6.0 | Ended Nov 2024 | Yes |
| 7.4 LTS | .NET 8.0 | Until Nov 2026 | Yes |

### Why .NET 8.0?

- Aligns with PowerShell 7.4 LTS (supported until November 2026)
- .NET 6 support ended November 2024
- Single build target simplifies testing and deployment
- Native library deployment is fully automatic
- Full API support for SQLite native library resolution

### Project Configuration

```xml
<TargetFramework>net8.0</TargetFramework>

<!-- SQLite packages -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.4" />
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.11" />
```

Native libraries deploy to `bin/Debug/net8.0/runtimes/{rid}/native/` during build. The build script (`PSReadLine.build.ps1`) restructures these into the flat `{rid}/` layout.

---

## Native Library Resolution Architecture

### How It Actually Works for Modules

PowerShell modules with native dependencies face a unique challenge: the module's `deps.json` is **not read** by the .NET native host layer. Only `pwsh.deps.json` is processed during startup.

1. `COREHOST_TRACE` only shows `pwsh.deps.json` processing — module deps.json is never read at the native host layer
2. PowerShell's `CorePsAssemblyLoadContext.NativeDllHandler` resolves native DLLs for modules
3. It builds: `Path.Combine(assemblyDir, runtimeIdentifier, libraryName) + extension`
4. e.g., `{moduleDir}/win-x64/e_sqlite3.dll`
5. If that fails, the OS default search (PATH, system dirs) is tried as a last resort

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

### deps.json runtimeTargets Are Dead Data for Modules

PSReadLine's `deps.json` correctly lists `runtimes/win-x64/native/e_sqlite3.dll` in `runtimeTargets`, but this is ONLY used when the app's `deps.json` is read (standalone apps). For PowerShell modules, this data is never consumed — confirmed via `COREHOST_TRACE` analysis.

### PSReadLine's Belt-and-Suspenders Resolver

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
    var rid = RuntimeInformation.RuntimeIdentifier;
    var libName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "e_sqlite3.dll" :
                  RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "libe_sqlite3.so" :
                  "libe_sqlite3.dylib";
    
    return new[] {
        Path.Combine(moduleRoot, rid, "native", libName),
        Path.Combine(moduleRoot, libName)
    };
}
```

---

## Module Distribution Layout

After `dotnet publish`, the build script (`PSReadLine.build.ps1`) restructures:
```
runtimes/win-x64/native/e_sqlite3.dll  →  win-x64/e_sqlite3.dll
runtimes/linux-x64/native/libe_sqlite3.so  →  linux-x64/libe_sqlite3.so
```

Final module layout:
```
PSReadLine/
├── Microsoft.PowerShell.PSReadLine.dll
├── Microsoft.PowerShell.Pager.dll
├── Microsoft.Data.Sqlite.dll
├── SQLitePCLRaw.core.dll
├── SQLitePCLRaw.batteries_v2.dll
├── SQLitePCLRaw.provider.e_sqlite3.dll
├── PSReadLine.psd1  (PowerShellVersion = '7.4')
├── PSReadLine.psm1
├── PSReadLine.format.ps1xml
├── win-x64/
│   └── e_sqlite3.dll
├── win-arm64/
│   └── e_sqlite3.dll
├── linux-x64/
│   └── libe_sqlite3.so
├── linux-arm64/
│   └── libe_sqlite3.so
├── linux-musl-x64/
│   └── libe_sqlite3.so
├── linux-musl-arm64/
│   └── libe_sqlite3.so
├── osx-x64/
│   └── libe_sqlite3.dylib
└── osx-arm64/
    └── libe_sqlite3.dylib
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

---

## Future Improvements

Three complementary approaches to fix native library resolution for the PowerShell ecosystem:

1. **PowerShell engine fix** (recommended): Add `runtimes/{rid}/native/` as a fallback probe path in `NativeDllHandler`. ~5 line change, benefits all modules.
2. **PSResourceGet restructuring**: Handle `runtimes/` → `{rid}/` layout transformation during `Install-PSResource`. Benefits all installed modules.
3. **Module-shipped resolver**: PSReadLine can ship its own `NativeLibrary.SetDllImportResolver` as belt-and-suspenders for older PowerShell versions.

---

## Troubleshooting

**Native DLL not found**:
- PSReadLine's `deps.json` is NOT read by the .NET host — only `pwsh.deps.json` is processed at startup
- Verify native libraries exist in flat `{rid}/` folders: e.g., `bin/Debug/net8.0/win-x64/e_sqlite3.dll`
- If `runtimes/` exists but `win-x64/` etc. don't, the build script restructuring didn't run
- Use `(Get-Process -Id $PID).Modules | Where-Object ModuleName -match 'sqlite'` to see what actually loaded
- A random `e_sqlite3.dll` on PATH (from other tools) can mask the real problem

**Multiple SQLite versions**: 
- All SQLitePCLRaw packages must use the same version (currently 2.1.11)
- Check `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3` versions match

**Linux/macOS library loading**: 
- Library names: `libe_sqlite3.so` (Linux), `libe_sqlite3.dylib` (macOS), `e_sqlite3.dll` (Windows)
- Alpine Linux uses `linux-musl-x64` or `linux-musl-arm64` RIDs

**winsqlite3 fallback**:
- If `SQLitePCLRaw.provider.winsqlite3.dll` is in the build output, SQLitePCLRaw may use Windows' built-in `C:\WINDOWS\SYSTEM32\winsqlite3.DLL` instead of `e_sqlite3`
- This works on Windows but is not the intended provider — verify with `Get-Process` modules output

**Key findings from native library investigation (March 2026)**:
- Module `deps.json` `runtimeTargets` are **not consumed** by the .NET host for PowerShell modules (confirmed via `COREHOST_TRACE`)
- Only `pwsh.deps.json` is read at startup; module loading is handled by `CorePsAssemblyLoadContext`
- PowerShell's `NativeDllHandler` expects flat `{rid}/{libraryName}` layout, not NuGet's `runtimes/{rid}/native/`
- The NuGet `runtimes/` convention has existed since 2016; PowerShell's flat convention since 2018 — they were never aligned

## Performance Considerations

**SQLite vs Text History**:

| | Text | SQLite |
|---|---|---|
| Pros | Simple, no dependencies, human-readable | Fast indexed queries O(log n), deduplication, rich queries, transactional integrity |
| Cons | Linear search O(n), duplicates, no location/time queries | Requires native library, binary format |

**Optimization Tips**:
1. Use prepared statements for repeated queries
2. Index key columns: `CommandHash`, `LastExecuted`, `ExecutionCount`
3. Batch inserts in transactions for better performance
4. WAL mode for concurrent read/write: `PRAGMA journal_mode=WAL;`
5. Lazy initialization: Only open database when history is accessed
