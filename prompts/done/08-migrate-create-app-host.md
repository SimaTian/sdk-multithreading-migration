# Prompt: Migrate CreateAppHost to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/CreateAppHost.cs`

## What This Task Does
Creates the runtime host for an application by embedding the app DLL path into the apphost executable. Uses `HostWriter.CreateAppHost()` from `Microsoft.NET.HostModel`.

## Forbidden API Usage Found
```csharp
HostWriter.CreateAppHost(
    appHostSourceFilePath: AppHostSourcePath,
    appHostDestinationFilePath: AppHostDestinationPath,
    appBinaryFilePath: AppBinaryName,
    windowsGraphicalUserInterface: isGUI,
    assemblyToCopyResourcesFrom: resourcesAssembly,  // = IntermediateAssembly
    enableMacOSCodeSign: EnableMacOSCodeSign,
    disableCetCompat: DisableCetCompat,
    dotNetSearchOptions: options);
```
- `AppHostSourcePath` — source apphost binary, used in file read operations
- `AppHostDestinationPath` — destination path, used in file write operations
- `IntermediateAssembly` (aliased as `resourcesAssembly`) — assembly to copy resources from
- `AppBinaryName` — embedded in the apphost (might not need absolutization if it's just a name, not a path)

All of these flow into `HostWriter.CreateAppHost()` which performs file I/O internally.

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Absolutize `AppHostSourcePath`, `AppHostDestinationPath`, `IntermediateAssembly` before passing to `HostWriter.CreateAppHost()`
4. `AppBinaryName` — analyze if this is a filename-only (no path resolution needed) or a path

### Expected Changes
```csharp
protected override void ExecuteCore()
{
    try
    {
        var isGUI = WindowsGraphicalUserInterface;
        AbsolutePath resourcesAssembly = TaskEnvironment.GetAbsolutePath(IntermediateAssembly);
        AbsolutePath appHostSource = TaskEnvironment.GetAbsolutePath(AppHostSourcePath);
        AbsolutePath appHostDest = TaskEnvironment.GetAbsolutePath(AppHostDestinationPath);
        
        // ... retry loop ...
        HostWriter.CreateAppHost(
            appHostSourceFilePath: appHostSource,
            appHostDestinationFilePath: appHostDest,
            appBinaryFilePath: AppBinaryName,  // likely just a filename
            assemblyToCopyResourcesFrom: resourcesAssembly,
            ...);
    }
    // ... exception handling ...
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