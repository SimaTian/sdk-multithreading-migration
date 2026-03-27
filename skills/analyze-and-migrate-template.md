# Skill: Analyze-and-Migrate Task Template (TDD Approach)

## When to Use
Use this template when migrating a task that MAY or MAY NOT use forbidden APIs. The agent must read the full task source, determine the correct migration approach, and follow a strict TDD workflow.

## Core Principle: Tests First, Migration Second

**Every migration follows this strict order:**
1. Analyze the task → 2. Write behavioral tests → 3. Apply stub migration (attribute + interface + property only) → 4. Verify tests FAIL for behavioral reasons → 5. Complete the migration (absolutize paths) → 6. Verify tests PASS → 7. Verify no regressions

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

6. **Output properties must preserve input form (relative stays relative)**: The migration absolutizes paths *internally* for correct file I/O, but Output properties exposed to MSBuild consumers must preserve the original form of the input. If a task's Output is derived from its Input items (e.g., `ExcludedFiles` is a subset of `FilesToBundle`), and those inputs were relative paths, the outputs must remain relative — NOT absolutized. Test this by providing relative input paths and asserting the Output property values are still relative after execution. This prevents breaking downstream MSBuild targets that depend on the original path form.

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

**CRITICAL: Check for transitive forbidden API usage through external library calls.** If the task calls a method from a NuGet package or external library, inspect what that method does internally. Library methods that call `Environment.GetEnvironmentVariable()`, `Path.GetFullPath()`, `File.*`, or other forbidden APIs bypass `TaskEnvironment` entirely — the library has no knowledge of it. These are the hardest violations to catch because they don't appear in the task's own source code.

Known examples:
- `DotNetReferenceAssembliesPathResolver.Resolve()` (from `Microsoft.Extensions.DependencyModel`) — internally calls `Environment.GetEnvironmentVariable("DOTNET_REFERENCE_ASSEMBLIES_PATH")` and `Directory.Exists()` on hardcoded paths, bypassing `TaskEnvironment`.

**Remediation**: Replace the library call with inline code that routes through `TaskEnvironment`. For env var reads, use `taskEnvironment.GetEnvironmentVariable(...)`. For directory probes, absolutize paths first. If the library method is complex, document it as a known limitation with a TODO comment linking to the library source.

**Step 2b: Verify injection completeness.** If the task (or any helper class it uses) was refactored to accept an injected delegate (e.g., `Func<string, string?>` for env vars), verify that ALL code paths in that class use the delegate. Search for any remaining direct calls to `Environment.GetEnvironmentVariable`, `Path.GetFullPath`, or library methods that internally call these. A class that accepts an injection delegate but still has static bypasses is worse than no refactoring — it gives false confidence that all reads are routed when they're not. Treat leaked static calls as bugs, not TODOs.

### Step 3: Write Failing Tests FIRST (before any task code changes)
Create test file in `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/`.

**IMPORTANT: Do NOT write tests for attribute or interface presence** (e.g., `BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>()` or `BeAssignableTo<IMultiThreadableTask>()`). These are redundant noise — they verify decoration, not correctness. Focus **all** test effort on behavioral tests.

**For attribute-only tasks** (no forbidden APIs found):
Write a behavioral smoke test that constructs the task with `TaskEnvironment`, runs it with minimal/empty inputs, and asserts it completes without unexpected errors.

**For interface-based tasks** (forbidden APIs found):
Write path-resolution, multi-process/multi-threaded parity, and output-relativity tests per interface-migration-template.md.

### Step 4: Apply Stub Migration
Add the attribute, interface, and `TaskEnvironment` property — but do NOT change any logic. The task code still uses its original path resolution (e.g., raw `Path.GetFullPath()` or passing relative paths to libraries). This makes the tests compile and run.

**For attribute-only tasks:**
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase
{
    // ... unchanged code ...
}
```

**For interface-based tasks:**

**IMPORTANT — `TaskEnvironment` property declaration must be dual-targeted.** The SDK tasks build for both `net11.0` and `net472`. On .NET (non-NETFRAMEWORK), nullable analysis requires `= null!` since MSBuild sets the property before `Execute()`. On NETFRAMEWORK, use a backing field with `TaskEnvironmentDefaults.Create()` fallback:

```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
#if NETFRAMEWORK
    private TaskEnvironment _taskEnvironment;
    public TaskEnvironment TaskEnvironment
    {
        get => _taskEnvironment ??= TaskEnvironmentDefaults.Create();
        set => _taskEnvironment = value;
    }
#else
    public TaskEnvironment TaskEnvironment { get; set; } = null!;
#endif
    // ... NO other changes — code still uses original path resolution ...
}
```

### Step 5: Verify Tests FAIL for Behavioral Reasons
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~TheTaskMultiThreading"
```
**ALL tests MUST compile and run, but FAIL.** A test that passes against a stub-migrated task is a no-op — it does not validate the migration and must be redesigned. Go through each test individually:

**Critical design rule: every test must set CWD to a decoy directory.** The stub migration adds a `TaskEnvironment` property but doesn't use it — the task still resolves paths against the process CWD. Tests that leave CWD at its default (or set it to the projectDir) will pass even with the stub, because CWD-based resolution happens to find the files. To catch this:
- Create files **only** under `projectDir`
- Set CWD to a **different, empty** `decoyDir` before running the task
- The task receives `TaskEnvironment.CreateForTest(projectDir)` — if it uses TaskEnvironment, it finds files; if it uses CWD, it gets `FileNotFoundException`/`DirectoryNotFoundException`

**How each test type should fail against the stub:**
1. **Parity test** — Runs once with CWD==projectDir, once with CWD==otherDir. With the stub, the first run finds files (CWD matches), the second doesn't → different exception types/messages → assertion fails.
2. **Output-relativity test** — Sets CWD to decoyDir. With the stub, task can't find files via CWD → throws `FileNotFoundException` → test asserts this is NOT a file-not-found error.
3. **Concurrent execution test** — Sets CWD to a shared decoyDir. Each thread has its own projectDir with files. With the stub, all threads resolve via shared CWD (decoyDir) instead of their own TaskEnvironment → `FileNotFoundException`/`DirectoryNotFoundException` → test asserts no file-not-found exceptions.

### Step 6: Complete the Migration
Now apply the actual path absolutization changes.
Follow the interface-migration-template.md skill for detailed replacement steps.

### Step 7: Verify Tests PASS
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~TheTaskMultiThreading"
```
**All new tests MUST pass now.**

### Step 8: Verify No Regressions
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
