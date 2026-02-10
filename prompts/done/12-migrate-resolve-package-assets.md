# Prompt: Migrate ResolvePackageAssets to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/ResolvePackageAssets.cs`

## What This Task Does
This is the LARGEST and MOST COMPLEX task in the project. It resolves NuGet package assets (compile references, runtime assemblies, native libraries, content files, analyzers, etc.) from the lock file into MSBuild items. It uses a binary cache file for performance.

## Forbidden API Usage (Expected — Must Analyze Full File)
This file is ~2000+ lines. The agent must read the full file and trace all path usage. Expected forbidden APIs:
- `Path.GetFullPath(...)` — likely used for path canonicalization
- `File.Exists(...)` — used to check cache files, lock files, package directories
- `File.Open(...)` / `FileStream` — used for cache file read/write
- `Path.Combine(...)` with potentially relative base paths
- Properties like `ProjectAssetsFile`, `ProjectAssetsCacheFile`, `ProjectPath` — used in file operations

Key properties to trace:
- `ProjectAssetsFile` — lock file path, read from disk
- `ProjectAssetsCacheFile` — binary cache file path, read/written
- `ProjectPath` — project file path, used in path resolution

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Read the ENTIRE file carefully and identify ALL file system API calls
4. Absolutize every path that flows into a file operation
5. Pay special attention to the cache file operations and lock file reading
6. Trace paths through internal helper methods and classes (e.g., `LockFileCache`, `CacheWriter`, `CacheReader`)

### IMPORTANT
This is the most complex migration. Take extra care to:
- Read every method in the file
- Trace every string that could be a path
- Check helper classes for indirect file API usage
- Handle null/empty paths with guards before calling `GetAbsolutePath()`

## Workflow (TDD — strict order)

### Phase 1: Write Failing Tests FIRST (before any task code changes)
1. Read the task source file to understand its behavior and forbidden API usage (documented above)
2. Write unit tests in the UnitTests project that validate the migration
3. These tests MUST FAIL against the current unmigrated code because:
   - The task doesn't implement `IMultiThreadableTask` yet (interface check fails)
   - The task doesn't have `[MSBuildMultiThreadableTask]` yet (attribute check fails, if not already present)
   - The task uses `Path.GetFullPath()` / raw file APIs instead of `TaskEnvironment` (path resolution test fails)

### Phase 2: Verify Tests FAIL
```bash
dotnet build src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests fail.** If any pass, the test is not properly validating the migration — fix the test before proceeding.

### Phase 3: Migrate the Task
Apply the code changes documented in the "Migration Required" / "Expected Changes" sections above.

### Phase 4: Verify Tests PASS
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests now pass.**

### Phase 5: Verify No Regressions
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```
**Confirm ALL existing tests still pass.**

## Acceptance Criteria
- [ ] Tests written FIRST, before any task code changes
- [ ] Tests verified to FAIL against unmigrated code
- [ ] Task migrated (attribute + interface + API replacements)
- [ ] New tests verified to PASS after migration
- [ ] All existing tests still pass (no regressions)
- [ ] `dotnet build src/Tasks/Microsoft.NET.Build.Tasks/Microsoft.NET.Build.Tasks.csproj` succeeds