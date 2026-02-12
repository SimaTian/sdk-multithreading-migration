# Skill: Analyze-and-Migrate Task Template (TDD Approach)

## When to Use
Use this template when migrating a task that MAY or MAY NOT use forbidden APIs. The agent must read the full task source, determine the correct migration approach, and follow a strict TDD workflow.

## Core Principle: Tests First, Migration Second

**Every migration follows this strict order:**
1. Analyze the task → 2. Write failing tests → 3. Verify tests FAIL → 4. Migrate the code → 5. Verify tests PASS → 6. Verify no regressions

## Test Design Rules

1. **Do NOT generate tests for attribute or interface implementation** (`[MSBuildMultiThreadableTask]`, `IMultiThreadableTask`). These are trivially verified and don't tell us anything about correctness. Focus all test effort on behavioral correctness.

2. **Test output correctness against multi-process baseline**: The IMultiThreadableTask migration should produce identical outputs to the original single-threaded execution. Test by:
   - Running the task in single-threaded mode (no TaskEnvironment, CWD-based) as baseline
   - Running the task in multi-threaded mode (with TaskEnvironment) 
   - Asserting both produce identical Output properties and log messages

3. **Use reflection-based test harness where possible**: To avoid duplicating test boilerplate across 46+ tasks, create a generalized test helper that:
   - Discovers all public Input/Output properties via reflection
   - Sets Input properties with test values
   - Verifies Output properties contain project-relative paths (not CWD-relative)
   - Checks log messages don't contain CWD-based paths
   - Only write task-specific tests when the task has unique behavior that generic tests can't cover

4. **Local stress testing (not committed)**: Run concurrent execution tests locally during migration to verify thread safety, but do NOT include stress tests in the final committed test suite. These are exploratory validation only.

5. **Preserve all public properties**: Every public property on the original task (Input, Output, or plain) must exist on the migrated task. Dropping, renaming, or changing the type of any public property is a migration error.

## Process

### Step 1: Clone & Read
```bash
git clone https://github.com/SimaTian/sdk.git && cd sdk && git checkout main
```
Read the task source file completely.

### Step 2: Analyze for Forbidden APIs
Search the task code for ALL of these patterns:
- `Path.GetFullPath(` — needs TaskEnvironment.GetAbsolutePath()
- `File.Exists(`, `File.Open(`, `File.Create(`, `File.ReadAllText(`, `File.WriteAllText(`, `File.Delete(`, `File.Copy(`, `File.Move(` — paths must be absolute
- `Directory.Exists(`, `Directory.CreateDirectory(`, `Directory.Delete(` — paths must be absolute
- `new FileStream(`, `new StreamReader(`, `new StreamWriter(` — paths must be absolute
- `XDocument.Load(`, `XDocument.Save(` — paths must be absolute
- `FileVersionInfo.GetVersionInfo(` — path must be absolute
- `Environment.GetEnvironmentVariable(`, `Environment.SetEnvironmentVariable(` — needs TaskEnvironment
- `Environment.CurrentDirectory` — needs TaskEnvironment.ProjectDirectory
- `new ProcessStartInfo(`, `Process.Start(` — needs TaskEnvironment.GetProcessStartInfo()
- `Environment.Exit(`, `Environment.FailFast(` — forbidden, must remove
- `Console.` — forbidden

Also trace path strings through helper method calls — a path might flow into a helper that internally uses File APIs.

### Step 3: Write Failing Tests FIRST (before any task code changes)
Create test file in `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/`.

**For attribute-only tasks** (no forbidden APIs found):
```csharp
[Fact]
public void ItHasMultiThreadableAttribute()
{
    typeof(TheTask).Should().BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>();
}
```
This test FAILS because the attribute hasn't been added yet.

**For interface-based tasks** (forbidden APIs found):
Write interface check, attribute check, AND path-resolution tests per interface-migration-template.md. All will FAIL against unmigrated code.

### Step 4: Verify Tests FAIL
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~TheTaskMultiThreading"
```
**All new tests MUST fail.** If any pass, the test is not validating the migration — fix the test.

### Step 5: Apply the Migration

**If NO forbidden APIs → Attribute-Only:**
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase  // NO IMultiThreadableTask needed
{
    // ... unchanged code ...
}
```

**If forbidden APIs found → Interface-Based:**
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; }
    // ... modify code to use TaskEnvironment ...
}
```
Follow the interface-migration-template.md skill for detailed replacement steps.

### Step 6: Verify Tests PASS
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~TheTaskMultiThreading"
```
**All new tests MUST pass now.**

### Step 7: Verify No Regressions
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```
**All existing tests MUST still pass.**

## ToolTask Subclasses
Some tasks extend `Microsoft.Build.Utilities.ToolTask` instead of `TaskBase`. These:
- Cannot simply add `IMultiThreadableTask` since ToolTask already implements ITask
- The attribute approach works fine: `[MSBuildMultiThreadableTask]`
- Interface approach requires implementing `IMultiThreadableTask` on the ToolTask subclass directly
- `ToolTask` has its own `Execute()` flow — analyze `ValidateParameters()`, `GenerateCommandLineCommands()`, `GenerateResponseFileCommands()`, `ExecuteTool()` for path usage
