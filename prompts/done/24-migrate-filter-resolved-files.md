# Prompt: Migrate FilterResolvedFiles for Multithreading

## Skill References
- `files/skills/multithreaded-task-migration.md` - repo layout, API reference, forbidden APIs, test patterns
- `files/skills/analyze-and-migrate-template.md` - analysis process and migration steps
- `files/skills/interface-migration-template.md` - detailed interface-based migration template (if needed)

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/FilterResolvedFiles.cs`

## What This Task Does
Filters resolved files based on criteria like publish readiness.

## Workflow (TDD — strict order)

### Phase 1: Analyze
1. Clone the repository and checkout the `main` branch
2. Read the FULL task source file from the repository
3. Analyze for ALL forbidden API usage per the analyze-and-migrate-template skill:
   - `Path.GetFullPath`, `File.*`, `Directory.*`, `FileStream`, `StreamReader/Writer`
   - `Environment.GetEnvironmentVariable/SetEnvironmentVariable/CurrentDirectory`
   - `ProcessStartInfo`, `Process.Start`
   - Trace all path strings through helper methods
4. Determine: attribute-only (no forbidden APIs) or interface-based (forbidden APIs found)

### Phase 2: Write Failing Tests FIRST (before any task code changes)
Create test(s) in `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/`:
- **Attribute check**: `typeof(FilterResolvedFiles).Should().BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>()`
- **If interface-based**: `typeof(FilterResolvedFiles).Should().BeAssignableTo<IMultiThreadableTask>()` + path resolution test using TaskEnvironment with a project dir different from CWD
- Tests MUST be designed to FAIL against the current unmigrated code

### Phase 3: Verify Tests FAIL
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests fail.** If any test passes, it's not validating the migration — fix the test before proceeding.

### Phase 4: Migrate the Task
- If NO forbidden APIs: add `[MSBuildMultiThreadableTask]` attribute only
- If forbidden APIs found: add attribute + implement `IMultiThreadableTask` + replace APIs with TaskEnvironment equivalents

### Phase 5: Verify Tests PASS
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests now pass.**

### Phase 6: Verify No Regressions
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```
**Confirm ALL existing tests still pass.**

## Acceptance Criteria
- [ ] Task source fully analyzed for forbidden APIs — findings documented
- [ ] Tests written FIRST, before any task code changes
- [ ] Tests verified to FAIL against unmigrated code
- [ ] Correct migration applied (attribute-only OR attribute+interface)
- [ ] New tests verified to PASS after migration
- [ ] All existing tests still pass (no regressions)
- [ ] `dotnet build src/Tasks/Microsoft.NET.Build.Tasks/Microsoft.NET.Build.Tasks.csproj` succeeds