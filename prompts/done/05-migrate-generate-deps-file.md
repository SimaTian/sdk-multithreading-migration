# Prompt: Migrate GenerateDepsFile to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/GenerateDepsFile.cs`

## What This Task Does
Generates the `$(project).deps.json` file. Large task that creates a DependencyContext from NuGet lock file data, project references, runtime pack assets, and writes it as JSON.

## Forbidden API Usage Found
In `WriteDepsFile(string depsFilePath)`:
```csharp
using (var fileStream = File.Create(depsFilePath))
{
    writer.Write(dependencyContext, fileStream);
}
_filesWritten.Add(new TaskItem(depsFilePath));
```
- `depsFilePath` (from `DepsFilePath` property) — passed to `File.Create()`, could be relative

Also check:
- `ProjectPath` — used in `SingleProjectInfo.Create(ProjectPath, ...)` — might flow into file operations
- `AssetsFilePath` — used in `new LockFileCache(this).GetLockFile(AssetsFilePath)` — verify if LockFileCache uses it in file ops
- `RuntimeGraphPath` — used in `new RuntimeGraphCache(this).GetRuntimeGraph(RuntimeGraphPath)` — verify

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `DepsFilePath` before `File.Create()`
4. Trace `ProjectPath`, `AssetsFilePath`, `RuntimeGraphPath` through helper classes to verify they reach file APIs — absolutize if needed
5. Keep `_filesWritten.Add(new TaskItem(depsFilePath))` using the original path (or absolutized — check what consumers expect)

### Key Path Tracing
- `LockFileCache.GetLockFile(path)` — likely uses `File.Open(path)` internally, so `AssetsFilePath` needs absolutization
- `RuntimeGraphCache.GetRuntimeGraph(path)` — likely reads file, so `RuntimeGraphPath` needs absolutization
- `SingleProjectInfo.Create(ProjectPath, ...)` — uses ProjectPath for display/metadata, may not hit file system directly

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