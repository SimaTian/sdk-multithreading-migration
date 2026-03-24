# Skill: Interface-Based Task Migration Template (TDD Approach)

## Core Principle: Tests First, Migration Second

This migration follows a strict workflow:
1. **Read & analyze** the task for forbidden APIs
2. **Write behavioral tests** — path resolution, multi-process/multi-threaded parity, output relativity
3. **Apply stub migration** — attribute + interface + TaskEnvironment property only, NO logic changes
4. **Verify tests FAIL for behavioral reasons** — proves tests are not no-ops
5. **Complete the migration** — absolutize paths via TaskEnvironment
6. **Verify the tests pass** — run them and confirm success
7. **Verify existing tests still pass** — no regressions

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

**Do NOT write tests for attribute or interface presence** (e.g., `BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>()` or `BeAssignableTo<IMultiThreadableTask>()`). These are redundant noise — they verify decoration, not correctness.

**Focus on behavioral tests only:**

1. **Path resolution test** — Creates a temp directory as "project dir" (different from CWD), sets `TaskEnvironment` with that dir, provides relative paths, creates test fixtures under the project dir (NOT CWD), runs the task. Asserts errors are NOT about "file not found" — proving paths were resolved via TaskEnvironment.
2. **Multi-process vs multi-threaded parity test** — Runs the task with CWD==projectDir (multi-process mode) then CWD==otherDir (multi-threaded mode), asserts identical results/errors in both.
3. **Output relativity test** — If the task's Output is derived from Input items, provides relative input paths and asserts the output items preserve their relative ItemSpec values (not mutated to absolute).
4. **Concurrent execution test** — Runs N instances in parallel with isolated project dirs, asserts all produce the same outcome (no data races or shared-state corruption).

### Step 4: Apply Stub Migration
Add the attribute, interface, and `TaskEnvironment` property — but do NOT change any task logic. The task code still uses its original path resolution. This makes the tests compile and run.

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
Modify the task to actually use TaskEnvironment for path resolution:
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
    // TaskEnvironment property already added in Step 4 (dual-targeted)
    // ... existing code with path absolutization ...
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

## Test Design Principle: Behavioral Correctness Over Interface Checks

DO NOT write tests that merely check for attribute/interface presence. These add no value.

Instead, design tests that verify **output correctness**:
- The task receives relative paths as input
- `TaskEnvironment.ProjectDirectory` is set to a temp directory **different from** `Environment.CurrentDirectory`
- Required files/directories are created **only** under `ProjectDirectory`, NOT under CWD
- Run the task and verify it finds files under ProjectDirectory (not CWD)
- Verify log messages reference ProjectDirectory paths, not CWD paths
- If the task uses `Path.GetFullPath()` instead of `TaskEnvironment.GetAbsolutePath()`, it resolves against CWD and fails to find files

**Output properties must preserve input form (relative stays relative)**: The migration absolutizes paths *internally* for correct file I/O, but Output properties exposed to MSBuild consumers must preserve the original form of the input. If a task's Output is derived from its Input items (e.g., `ExcludedFiles` is a subset of `FilesToBundle`), and those inputs were relative paths, the outputs must remain relative — NOT absolutized. Test this by providing relative input paths and asserting the Output property values are still relative after execution. This prevents breaking downstream MSBuild targets that depend on the original path form.

**Reflection-based generalized testing**: Use reflection to enumerate Input/Output properties. For tasks with `string` Output properties that are NOT derived from inputs, assert they start with the ProjectDirectory path. For `ITaskItem[]` outputs derived from input items, assert ItemSpec values preserve their original relative form.

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
