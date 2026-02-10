# Prompt: Migrate GetDependsOnNETStandard to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Extensions.Tasks/GetDependsOnNETStandard.cs`
(Also has platform-specific partials: `GetDependsOnNETStandard.net46.cs`, `GetDependsOnNETStandard.netstandard.cs`)

## What This Task Does
Determines if any of the provided reference assemblies depend on `netstandard.dll`. Iterates through references, reads each assembly file to check for netstandard/System.Runtime dependencies.

## Forbidden API Usage Found
```csharp
private bool AnyReferenceDependsOnNETStandard()
{
    foreach (var reference in References ?? Array.Empty<ITaskItem>())
    {
        var referenceSourcePath = ItemUtilities.GetSourcePath(reference);
        if (referenceSourcePath != null && File.Exists(referenceSourcePath))
        {
            if (GetFileDependsOnNETStandard(referenceSourcePath))
                return true;
        }
    }
}
```
- `referenceSourcePath` — passed to `File.Exists()` and `GetFileDependsOnNETStandard()` which reads the file
- The path comes from `ItemUtilities.GetSourcePath(reference)` — may be relative

Also check the platform-specific partials:
- `GetDependsOnNETStandard.net46.cs` — likely contains `GetFileDependsOnNETStandard()` implementation using assembly loading
- `GetDependsOnNETStandard.netstandard.cs` — .NET Core implementation

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `referenceSourcePath` before `File.Exists()` and file reading operations
4. Check platform-specific partials for additional file API usage

### Expected Change
```csharp
private bool AnyReferenceDependsOnNETStandard()
{
    foreach (var reference in References ?? Array.Empty<ITaskItem>())
    {
        var referenceSourcePath = ItemUtilities.GetSourcePath(reference);
        if (referenceSourcePath != null)
        {
            AbsolutePath absoluteRefPath = TaskEnvironment.GetAbsolutePath(referenceSourcePath);
            if (File.Exists(absoluteRefPath))
            {
                if (GetFileDependsOnNETStandard(absoluteRefPath))
                    return true;
            }
        }
    }
}
```

## Important Note
This task is in `Microsoft.NET.Build.Extensions.Tasks`, NOT `Microsoft.NET.Build.Tasks`. 
- Build with: `dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks/Microsoft.NET.Build.Extensions.Tasks.csproj`
- Tests are in: `src/Tasks/Microsoft.NET.Build.Extensions.Tasks.UnitTests/`
- The Extensions.Tasks project also includes Common/ files, so the polyfills should be available

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
dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks.UnitTests/Microsoft.NET.Build.Extensions.Tasks.UnitTests.csproj
dotnet test src/Tasks/Microsoft.NET.Build.Extensions.Tasks.UnitTests/Microsoft.NET.Build.Extensions.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests fail.** If any pass, the test is not properly validating the migration — fix the test before proceeding.

### Phase 3: Migrate the Task
Apply the code changes documented in the "Migration Required" / "Expected Changes" sections above.

### Phase 4: Verify Tests PASS
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Extensions.Tasks.UnitTests/Microsoft.NET.Build.Extensions.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests now pass.**

### Phase 5: Verify No Regressions
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Extensions.Tasks.UnitTests/Microsoft.NET.Build.Extensions.Tasks.UnitTests.csproj
```
**Confirm ALL existing tests still pass.**

## Acceptance Criteria
- [ ] Tests written FIRST, before any task code changes
- [ ] Tests verified to FAIL against unmigrated code
- [ ] Task migrated (attribute + interface + API replacements)
- [ ] New tests verified to PASS after migration
- [ ] All existing tests still pass (no regressions)
- [ ] `dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks/Microsoft.NET.Build.Extensions.Tasks.csproj` succeeds