# Prompt: Migrate GetAssemblyAttributes to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/GetAssemblyAttributes.cs`

## What This Task Does
Reads assembly attributes (company, copyright, version, etc.) from a template file using `FileVersionInfo.GetVersionInfo()` and `FileUtilities.TryGetAssemblyVersion()`. Returns the attributes as MSBuild task items.

## Forbidden API Usage Found
```csharp
var fileVersionInfo = FileVersionInfo.GetVersionInfo(Path.GetFullPath(PathToTemplateFile));
Version assemblyVersion = FileUtilities.TryGetAssemblyVersion(Path.GetFullPath(PathToTemplateFile));
```
- `Path.GetFullPath(PathToTemplateFile)` — used TWICE, resolves relative to process CWD

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute to the class
2. Implement `IMultiThreadableTask` — add `public TaskEnvironment TaskEnvironment { get; set; }`
3. Replace both `Path.GetFullPath(PathToTemplateFile)` calls with `TaskEnvironment.GetAbsolutePath(PathToTemplateFile).GetCanonicalForm()` (GetCanonicalForm because the original used GetFullPath for canonicalization)
4. Store the absolutized path in a local variable to avoid calling it twice

### Expected Result
```csharp
[MSBuildMultiThreadableTask]
public class GetAssemblyAttributes : TaskBase, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; }

    protected override void ExecuteCore()
    {
        AbsolutePath templatePath = TaskEnvironment.GetAbsolutePath(PathToTemplateFile).GetCanonicalForm();
        var fileVersionInfo = FileVersionInfo.GetVersionInfo(templatePath);
        Version assemblyVersion = FileUtilities.TryGetAssemblyVersion(templatePath);
        // ... rest unchanged
    }
}
```

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