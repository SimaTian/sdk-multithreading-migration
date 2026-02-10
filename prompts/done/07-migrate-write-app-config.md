# Prompt: Migrate WriteAppConfigWithSupportedRuntime to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/WriteAppConfigWithSupportedRuntime.cs`

## What This Task Does
Loads an optional app.config XML file, adds a `<supportedRuntime>` element based on target framework, and writes the result to an intermediate output file.

## Forbidden API Usage Found
```csharp
// In ExecuteCore:
var fileStream = new FileStream(
    OutputAppConfigFile.ItemSpec,
    FileMode.Create, FileAccess.Write, FileShare.Read);

// In LoadAppConfig:
document = XDocument.Load(appConfigItem.ItemSpec);
```
- `OutputAppConfigFile.ItemSpec` — passed to `FileStream`, could be relative
- `appConfigItem.ItemSpec` (from `AppConfigFile`) — passed to `XDocument.Load()`, could be relative

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `OutputAppConfigFile.ItemSpec` before `FileStream` constructor
4. Absolutize `AppConfigFile.ItemSpec` before `XDocument.Load()` (with null guard since `AppConfigFile` is optional)

### Expected Changes
```csharp
protected override void ExecuteCore()
{
    XDocument doc = LoadAppConfig(AppConfigFile);
    AddSupportedRuntimeToAppconfig(doc, ...);

    AbsolutePath outputPath = TaskEnvironment.GetAbsolutePath(OutputAppConfigFile.ItemSpec);
    var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    // ...
}

private XDocument LoadAppConfig(ITaskItem appConfigItem)
{
    if (appConfigItem == null) { /* create empty doc */ }
    else
    {
        AbsolutePath configPath = TaskEnvironment.GetAbsolutePath(appConfigItem.ItemSpec);
        document = XDocument.Load(configPath);
        // ...
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