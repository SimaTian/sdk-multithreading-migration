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
- **Check external library method calls for transitive forbidden API usage** — if the task calls a method from a NuGet package, inspect what it does internally. Library methods that call `Environment.GetEnvironmentVariable()`, `Path.GetFullPath()`, or `Directory.Exists()` bypass `TaskEnvironment` entirely. Example: `DotNetReferenceAssembliesPathResolver.Resolve()` from `Microsoft.Extensions.DependencyModel` internally reads env vars and probes directories without `TaskEnvironment`.

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

**Do NOT add defensive `ProjectDirectory` self-initialization code** (e.g., reading `BuildEngine.ProjectFileOfTaskNode`). MSBuild creates a fully initialized `TaskEnvironment` — with `ProjectDirectory` already set — and assigns it via the property setter before `Execute()` is called. The task only needs `public TaskEnvironment TaskEnvironment { get; set; }`. For tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)`.

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

## Test Design Principle: Behavioral Correctness Over Interface Checks

DO NOT write tests that merely check for attribute/interface presence. These add no value.

Instead, design tests that verify **output correctness**:
- The task receives relative paths as input
- `TaskEnvironment.ProjectDirectory` is set to a temp directory **different from** `Environment.CurrentDirectory`
- Required files/directories are created **only** under `ProjectDirectory`, NOT under CWD
- Run the task and verify Output properties contain paths under ProjectDirectory
- Verify log messages reference ProjectDirectory paths, not CWD paths
- If the task uses `Path.GetFullPath()` instead of `TaskEnvironment.GetAbsolutePath()`, it resolves against CWD and fails to find files

**Reflection-based generalized testing**: Use reflection to enumerate Input/Output properties. For tasks with `string` Output properties, assert they start with the ProjectDirectory path. For `ITaskItem[]` outputs, assert ItemSpec values are rooted under ProjectDirectory. This eliminates repetitive per-task test code.

**Preserve public API surface**: The migrated task MUST have the exact same set of public properties as the original. Use reflection to compare before/after and fail if any property was removed, renamed, or retyped.

## Polyfills Available (from Phase 0)

These types exist in `src/Tasks/Common/` (gated `#if NETFRAMEWORK`) and from the MSBuild Framework package (for .NET):
- `IMultiThreadableTask` — interface with `TaskEnvironment TaskEnvironment { get; set; }`
- `TaskEnvironment` — class with `GetAbsolutePath()`, `GetEnvironmentVariable()`, `ProjectDirectory`, etc.
- `AbsolutePath` — struct with `Value`, `OriginalValue`, implicit string conversion, `GetCanonicalForm()`
- `MSBuildMultiThreadableTaskAttribute` — attribute (already existed)

In test project:
- `TaskEnvironmentHelper.CreateForTest()` — creates TaskEnvironment with CWD as project dir
- `TaskEnvironmentHelper.CreateForTest(string projectDirectory)` — creates TaskEnvironment with specified project dir

## Complete Migration Example: Path.GetFullPath Replacement

### Before (unsafe):
```csharp
[MSBuildMultiThreadableTask]
public class PathNormalizer : Microsoft.Build.Utilities.Task
{
    public string InputPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (string.IsNullOrEmpty(InputPath))
        {
            Log.LogError("InputPath is required.");
            return false;
        }
        // VIOLATION: Path.GetFullPath resolves relative to process CWD
        string resolvedPath = Path.GetFullPath(InputPath);
        Log.LogMessage(MessageImportance.Normal, $"Resolved path: {resolvedPath}");
        if (File.Exists(resolvedPath))
            Log.LogMessage(MessageImportance.Normal, $"File found at '{resolvedPath}'.");
        else
            Log.LogMessage(MessageImportance.Normal, $"File not found at '{resolvedPath}'.");
        return true;
    }
}
```

### After (correct migration):
```csharp
[MSBuildMultiThreadableTask]
public class PathNormalizer : Microsoft.Build.Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; }
    public string InputPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (string.IsNullOrEmpty(InputPath))
        {
            Log.LogError("InputPath is required.");
            return false;
        }

        // FIXED: Use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath
        // MSBuild sets TaskEnvironment (with ProjectDirectory) before calling Execute()
        string resolvedPath = TaskEnvironment.GetAbsolutePath(InputPath);
        Log.LogMessage(MessageImportance.Normal, $"Resolved path: {resolvedPath}");
        if (File.Exists(resolvedPath))
            Log.LogMessage(MessageImportance.Normal, $"File found at '{resolvedPath}'.");
        else
            Log.LogMessage(MessageImportance.Normal, $"File not found at '{resolvedPath}'.");
        return true;
    }
}
```

### Key differences:
1. Added `IMultiThreadableTask` interface
2. Added `TaskEnvironment` property (simple auto-property — MSBuild sets it before `Execute()`)
3. Replaced `Path.GetFullPath(InputPath)` → `TaskEnvironment.GetAbsolutePath(InputPath)`

**Do NOT add `ProjectDirectory` self-initialization code** (reading `BuildEngine.ProjectFileOfTaskNode` to set `TaskEnvironment.ProjectDirectory`). MSBuild handles this automatically by providing a fully initialized `TaskEnvironment` to all `IMultiThreadableTask` implementations before `Execute()` is called. For unit tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)` to set it manually.
