# Prompt: Migrate ResolvePackageDependencies to IMultiThreadableTask

## Skill References
- `files/skills/multithreaded-task-migration.md` — full context, repo layout, API reference
- `files/skills/interface-migration-template.md` — step-by-step migration and testing template

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/ResolvePackageDependencies.cs`

## What This Task Does
Raises NuGet LockFile representation to MSBuild items and resolves assets specified in the lock file. It processes package definitions, file definitions, package dependencies, and file dependencies from the assets file.

## Forbidden API Usage Found
```csharp
private string GetAbsolutePathFromProjectRelativePath(string path)
{
    return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProjectPath), path));
}
```
- `Path.GetFullPath(Path.Combine(...))` — resolves relative path using process CWD
- `ProjectPath` is used in `Path.GetDirectoryName(ProjectPath)` — might be relative itself

Also trace usage of `ResolvePackagePath` and `ResolveFilePath` which use `Path.Combine` and may pass relative paths.

## Migration Required
1. Add `[MSBuildMultiThreadableTask]` attribute
2. Implement `IMultiThreadableTask`
3. Replace `GetAbsolutePathFromProjectRelativePath` to use `TaskEnvironment.GetAbsolutePath()`
4. Verify `ProjectPath` is absolutized where used in path operations
5. Check `ResolveFilePath` — uses `Path.Combine(resolvedPackagePath, relativePath)` — if resolvedPackagePath is already absolute, this is fine

### Key Change
```csharp
private string GetAbsolutePathFromProjectRelativePath(string path)
{
    AbsolutePath projectDir = TaskEnvironment.GetAbsolutePath(Path.GetDirectoryName(ProjectPath));
    return Path.GetFullPath(Path.Combine(projectDir, path));
    // OR more correctly:
    // return new AbsolutePath(path, TaskEnvironment.GetAbsolutePath(Path.GetDirectoryName(ProjectPath))).GetCanonicalForm();
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