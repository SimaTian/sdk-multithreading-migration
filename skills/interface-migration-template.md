# Skill: Interface-Based Task Migration Template (TDD Approach)

## Core Principle: Tests First, Migration Second

This migration follows a strict TDD workflow:
1. **Read & analyze** the task for forbidden APIs
2. **Write tests that FAIL** against the current (unmigrated) code
3. **Verify the tests fail** — run them and confirm failure
4. **Migrate the task** — apply attribute, interface, replace APIs
5. **Verify the tests pass** — run them and confirm success
6. **Verify existing tests still pass** — no regressions

## Standard Migration Steps

### Step 1: Clone & Prepare
```bash
git clone https://github.com/SimaTian/sdk.git && cd sdk && git checkout main
```

### Step 2: Read & Analyze the Task File
Read the task source file and identify ALL forbidden API usage:
- `Path.GetFullPath(...)` calls
- `File.*` / `Directory.*` / `FileStream` / `StreamReader` / `StreamWriter` with potentially relative paths
- `Environment.GetEnvironmentVariable(...)` / `SetEnvironmentVariable(...)`
- `Environment.CurrentDirectory`
- `new ProcessStartInfo(...)` / `Process.Start(...)`
- Trace every path string variable through ALL method calls (including helpers/utilities)

### Step 3: Write Failing Tests FIRST (before any code changes)
Create a test file `GivenATheTaskMultiThreading.cs` in the UnitTests project.

**Design tests that will FAIL against the current unmigrated code:**

1. **Interface check test** — `typeof(TheTask).Should().BeAssignableTo<IMultiThreadableTask>()` — FAILS because the task doesn't implement it yet
2. **Attribute check test** — `typeof(TheTask).Should().BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>()` — FAILS if attribute not yet added
3. **Path resolution test** — Creates a temp directory as "project dir" (different from CWD), sets `TaskEnvironment` with that dir, provides relative paths, creates test fixtures under the project dir (NOT CWD), runs the task. This test FAILS on unmigrated code because:
   - The task doesn't have `TaskEnvironment` property yet, OR
   - The task uses `Path.GetFullPath()` which resolves against CWD, not the project dir, so it won't find files placed under the project dir

### Step 4: Verify Tests FAIL
```bash
dotnet build src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj --filter "FullyQualifiedName~TheTaskMultiThreading"
```
**All new tests MUST fail at this point.** If any pass, the test isn't testing the migration correctly — redesign it.

### Step 5: Migrate the Task
Now modify the task class:
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; }
    // ... existing code ...
}
```

Replace forbidden APIs **directly** — do NOT null-check `TaskEnvironment`. MSBuild always provides a `TaskEnvironment` instance to `IMultiThreadableTask` implementations, even in single-threaded mode (where it acts as a no-op passthrough):
- `Path.GetFullPath(x)` → `TaskEnvironment.GetAbsolutePath(x)` (add `.GetCanonicalForm()` if canonicalization was the intent)
- `File.Exists(relativePath)` → `File.Exists(TaskEnvironment.GetAbsolutePath(relativePath))`
- `new FileStream(path, ...)` → absolutize `path` first
- `XDocument.Load(path)` / `.Save(path)` → absolutize `path` first

Store absolutized path in a local variable for reuse:
```csharp
AbsolutePath absPath = TaskEnvironment.GetAbsolutePath(inputPath);
// use absPath (implicitly converts to string) in all subsequent file operations
```

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

## Test Design Principle: Tests Must Break on Unmigrated Code

Design tests so that they FAIL if the migration hasn't been done:
- The task receives relative paths as input
- `TaskEnvironment.ProjectDirectory` is set to a temp directory **different from** `Environment.CurrentDirectory`
- Required files/directories are created **only** under `ProjectDirectory`, NOT under CWD
- If the task uses `Path.GetFullPath()` instead of `TaskEnvironment.GetAbsolutePath()`, it resolves against CWD and fails to find files
- If the task doesn't implement `IMultiThreadableTask`, the interface check fails
- If the task doesn't have the attribute, the attribute check fails

## Polyfills Available (from Phase 0)

These types exist in `src/Tasks/Common/` (gated `#if NETFRAMEWORK`) and from the MSBuild Framework package (for .NET):
- `IMultiThreadableTask` — interface with `TaskEnvironment TaskEnvironment { get; set; }`
- `TaskEnvironment` — class with `GetAbsolutePath()`, `GetEnvironmentVariable()`, `ProjectDirectory`, etc.
- `AbsolutePath` — struct with `Value`, `OriginalValue`, implicit string conversion, `GetCanonicalForm()`
- `MSBuildMultiThreadableTaskAttribute` — attribute (already existed)

In test project:
- `TaskEnvironmentHelper.CreateForTest()` — creates TaskEnvironment with CWD as project dir
- `TaskEnvironmentHelper.CreateForTest(string projectDirectory)` — creates TaskEnvironment with specified project dir
