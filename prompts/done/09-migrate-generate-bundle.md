# Prompt: Migrate GenerateBundle to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/GenerateBundle.cs`

## What This Task Does
Creates a single-file bundle for .NET apps. Uses the `Bundler` class from `Microsoft.NET.HostModel.Bundle` to combine files into a single executable. Also implements `ICancelableTask` for cancellation support.

## Forbidden API Usage Found
```csharp
var bundler = new Bundler(
    AppHostName,
    OutputDir,       // <-- used as output directory for file operations
    options, ...);

foreach (var item in FilesToBundle)
{
    fileSpec.Add(new FileSpec(
        sourcePath: item.ItemSpec,          // <-- file paths, could be relative
        bundleRelativePath: item.GetMetadata(MetadataKeys.RelativePath)));
}

await DoWithRetry(() => bundler.GenerateBundle(fileSpec));
```
- `OutputDir` — passed to `Bundler` constructor, used as directory for file writes
- `FilesToBundle[].ItemSpec` — source file paths passed to `FileSpec`, used in file reads
- `AppHostName` — likely just a filename, not a path

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `OutputDir` before passing to `Bundler`
4. Absolutize each `item.ItemSpec` in `FilesToBundle` before passing to `FileSpec`
5. `AppHostName` — analyze if it's a path or just a filename

### Expected Changes
```csharp
private async Task ExecuteWithRetry()
{
    // ... platform detection code ...
    
    AbsolutePath absoluteOutputDir = TaskEnvironment.GetAbsolutePath(OutputDir);
    
    var bundler = new Bundler(
        AppHostName,
        absoluteOutputDir,
        options, ...);

    var fileSpec = new List<FileSpec>(FilesToBundle.Length);
    foreach (var item in FilesToBundle)
    {
        AbsolutePath sourcePath = TaskEnvironment.GetAbsolutePath(item.ItemSpec);
        fileSpec.Add(new FileSpec(
            sourcePath: sourcePath,
            bundleRelativePath: item.GetMetadata(MetadataKeys.RelativePath)));
    }
    // ...
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