# Prompt: Migrate ResolveAppHosts to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/ResolveAppHosts.cs`

## What This Task Does
Resolves app host packs for the target framework and runtime identifier. Determines paths to apphost, singlefilehost, comhost, and ijwhost binaries. Downloads missing packs if needed.

## Forbidden API Usage Found
```csharp
// In GetHostItem:
string hostRelativePathInPackage = Path.Combine("runtimes", bestAppHostRuntimeIdentifier, "native", ...);

if (!string.IsNullOrEmpty(TargetingPackRoot))
{
    appHostPackPath = Path.Combine(TargetingPackRoot, hostPackName, appHostPackVersion);
}
if (appHostPackPath != null && Directory.Exists(appHostPackPath))
{
    appHostItem.SetMetadata(MetadataKeys.PackageDirectory, appHostPackPath);
    appHostItem.SetMetadata(MetadataKeys.Path, Path.Combine(appHostPackPath, hostRelativePathInPackage));
}
```
- `TargetingPackRoot` — used in `Path.Combine()`, could be relative
- `appHostPackPath` — passed to `Directory.Exists()`, derived from potentially relative root
- `RuntimeGraphPath` — used in `new RuntimeGraphCache(this).GetRuntimeGraph(RuntimeGraphPath)` which reads a file
- `NetCoreTargetingPackRoot` — used in error messages (may not need absolutization for display)

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `TargetingPackRoot` if non-empty before using in `Path.Combine` / `Directory.Exists`
4. Absolutize `RuntimeGraphPath` before passing to `RuntimeGraphCache`
5. `NetCoreTargetingPackRoot` — only used in error messages, analyze if absolutization needed

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