# Prompt: Migrate GenerateRuntimeConfigurationFiles to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/GenerateRuntimeConfigurationFiles.cs`

## What This Task Does
Generates `$(project).runtimeconfig.json` and optionally `$(project).runtimeconfig.dev.json` files containing framework references, runtime options, and additional probing paths.

## Forbidden API Usage Found
In `ExecuteCore()` and helper methods:
```csharp
// In WriteRuntimeConfig:
WriteToJsonFile(RuntimeConfigPath, config);
_filesWritten.Add(new TaskItem(RuntimeConfigPath));

// In WriteDevRuntimeConfig:
WriteToJsonFile(RuntimeConfigDevPath, devConfig);
_filesWritten.Add(new TaskItem(RuntimeConfigDevPath));

// In AddUserRuntimeOptions:
if (string.IsNullOrEmpty(UserRuntimeConfig) || !File.Exists(UserRuntimeConfig))
    return;
using (JsonTextReader reader = new(File.OpenText(UserRuntimeConfig)))

// In WriteToJsonFile:
using (JsonTextWriter writer = new(new StreamWriter(File.Create(fileName))))

// AssetsFilePath used in:
LockFile lockFile = new LockFileCache(this).GetLockFile(AssetsFilePath);
```

Summary of paths needing absolutization:
- `RuntimeConfigPath` — passed to `File.Create()`
- `RuntimeConfigDevPath` — passed to `File.Create()`
- `UserRuntimeConfig` — passed to `File.Exists()` and `File.OpenText()`
- `AssetsFilePath` — passed to `LockFileCache.GetLockFile()` which opens the file

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `RuntimeConfigPath` before `WriteToJsonFile()`
4. Absolutize `RuntimeConfigDevPath` before `WriteToJsonFile()`
5. Absolutize `UserRuntimeConfig` before `File.Exists()` and `File.OpenText()`
6. Absolutize `AssetsFilePath` before passing to `LockFileCache`

### Key Consideration
- `WriteToJsonFile` is a static helper — pass the absolutized path to it
- `_filesWritten.Add(new TaskItem(...))` — use the absolutized path for consistency
- Guard `UserRuntimeConfig` absolutization with null/empty check since `GetAbsolutePath()` throws on null/empty

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