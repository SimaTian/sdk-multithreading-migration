# Prompt: Migrate ShowPreviewMessage for Multithreading

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/ShowPreviewMessage.cs`

## What This Task Does
Logs a "using preview SDK" message once per build. Uses `BuildEngine4.GetRegisteredTaskObject` / `RegisterTaskObject` to ensure the message is only shown once across all project evaluations in a build.

## Current Code
```csharp
public class ShowPreviewMessage : TaskBase
{
    protected override void ExecuteCore()
    {
        const string previewMessageKey = "Microsoft.NET.Build.Tasks.DisplayPreviewMessageKey";
        object messageDisplayed = BuildEngine4.GetRegisteredTaskObject(previewMessageKey, RegisteredTaskObjectLifetime.Build);
        if (messageDisplayed == null)
        {
            Log.LogMessage(MessageImportance.High, Strings.UsingPreviewSdk);
            BuildEngine4.RegisterTaskObject(previewMessageKey, new object(), RegisteredTaskObjectLifetime.Build, true);
        }
    }
}
```

## Analysis
This task does NOT use any forbidden file/path APIs. It uses `BuildEngine4.GetRegisteredTaskObject` which is MSBuild engine state, not process state. The MSBuild engine manages synchronization of `RegisteredTaskObject` APIs.

However, this task does NOT have the `[MSBuildMultiThreadableTask]` attribute yet.

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute — this is primarily an attribute-only migration
2. Do NOT implement `IMultiThreadableTask` — no `TaskEnvironment` needed since there are no forbidden API calls
3. Verify that `BuildEngine4.GetRegisteredTaskObject/RegisterTaskObject` is thread-safe (it is — MSBuild engine handles synchronization)

### Expected Change
```csharp
[MSBuildMultiThreadableTask]
public class ShowPreviewMessage : TaskBase
{
    // ... unchanged ...
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