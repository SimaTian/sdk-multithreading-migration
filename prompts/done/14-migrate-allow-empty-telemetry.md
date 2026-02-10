# Prompt: Migrate AllowEmptyTelemetry for Multithreading

## Skill References
- `files/skills/multithreaded-task-migration.md` — repo layout, API reference
- `files/skills/analyze-and-migrate-template.md` — analysis and migration steps

## Repository
https://github.com/SimaTian/sdk/tree/main

## Task File
`src/Tasks/Microsoft.NET.Build.Tasks/AllowEmptyTelemetry.cs`

## What This Task Does
Logs telemetry events via `IBuildEngine5.LogTelemetry()`. Processes event data items, optionally hashing values with SHA256.

## Pre-Analysis Notes
- Uses `BuildEngine as IBuildEngine5` for telemetry — this is MSBuild engine API, not process state
- Uses SHA256 for hashing — thread-safe
- No obvious file I/O, but agent MUST read the full file and verify

## Workflow (TDD — strict order)

### Phase 1: Analyze
1. Clone the repository and checkout the `main` branch
2. Read the FULL task source file from the repository
3. Analyze for ALL forbidden API usage per the analyze-and-migrate-template skill
4. Determine: attribute-only (no forbidden APIs) or interface-based (forbidden APIs found)

### Phase 2: Write Failing Tests FIRST (before any task code changes)
Create test(s) in `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/`:
- **Attribute check**: `typeof(AllowEmptyTelemetry).Should().BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>()`
- **If interface-based**: interface check + path resolution test
- Tests MUST be designed to FAIL against the current unmigrated code

### Phase 3: Verify Tests FAIL
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~MultiThreading"
```
**Confirm ALL new tests fail.** If any test passes, fix the test before proceeding.

### Phase 4: Migrate the Task
Apply the migration based on analysis results.

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