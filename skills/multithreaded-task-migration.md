# Skill: MSBuild Multithreaded Task Migration

## Context

This skill covers migrating MSBuild tasks in the `SimaTian/sdk` repository (branch `main`) to support multithreaded execution. The repository is at https://github.com/SimaTian/sdk/tree/main.

### Reference Documents
- [Thread-Safe Tasks Spec](https://github.com/dotnet/msbuild/blob/d58f712998dc831d3e3adcdb30ede24f6424348d/documentation/specs/multithreading/thread-safe-tasks.md)
- [Migration Skill Guide](https://github.com/dotnet/msbuild/blob/d58f712998dc831d3e3adcdb30ede24f6424348d/.github/skills/multithreaded-task-migration/SKILL.md)
- [AbsolutePath source](https://github.com/dotnet/msbuild/blob/main/src/Framework/PathHelpers/AbsolutePath.cs)
- [TaskEnvironment source](https://github.com/dotnet/msbuild/blob/main/src/Framework/TaskEnvironment.cs)
- [IMultiThreadableTask source](https://github.com/dotnet/msbuild/blob/main/src/Framework/IMultiThreadableTask.cs)

## Repository Layout

```
src/Tasks/
├── Common/                              # Shared code across task projects
│   ├── TaskBase.cs                      # Base class for most tasks (extends Microsoft.Build.Utilities.Task)
│   ├── MSBuildMultiThreadableTaskAttribute.cs  # EXISTING polyfill (#if NETFRAMEWORK)
│   ├── Logger.cs, LogAdapter.cs         # Logging infrastructure
│   ├── MetadataKeys.cs                  # Metadata key constants
│   └── Resources/Strings.resx          # Localized strings
├── Microsoft.NET.Build.Tasks/           # Main task library
│   ├── Microsoft.NET.Build.Tasks.csproj # Targets: net472 + $(SdkTargetFramework)
│   └── *.cs                             # Task implementations
├── Microsoft.NET.Build.Tasks.UnitTests/ # Unit tests (xUnit + FluentAssertions/AwesomeAssertions)
│   ├── Microsoft.NET.Build.Tasks.UnitTests.csproj
│   ├── Mocks/MockBuildEngine.cs         # IBuildEngine4 mock for tests
│   └── Given*.cs                        # Test files
├── Microsoft.NET.Build.Extensions.Tasks/
│   └── *.cs                             # Extension task implementations  
└── Microsoft.NET.Build.Extensions.Tasks.UnitTests/
```

## Key Classes

### TaskBase (src/Tasks/Common/TaskBase.cs)
```csharp
public abstract class TaskBase : Task
{
    internal new Logger Log { get; }
    public override bool Execute()  // catches BuildErrorException, logs telemetry
    protected abstract void ExecuteCore();
}
```

### MockBuildEngine (src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Mocks/MockBuildEngine.cs)
```csharp
internal class MockBuildEngine : IBuildEngine4
{
    public IList<BuildErrorEventArgs> Errors { get; }
    public IList<BuildMessageEventArgs> Messages { get; }
    public IList<BuildWarningEventArgs> Warnings { get; }
    public Dictionary<object, object> RegisteredTaskObjects { get; }
    // Implements GetRegisteredTaskObject, RegisterTaskObject, etc.
}
```

### Existing Polyfill Pattern (MSBuildMultiThreadableTaskAttribute.cs)
```csharp
#if NETFRAMEWORK
namespace Microsoft.Build.Framework
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class MSBuildMultiThreadableTaskAttribute : Attribute { }
}
#endif
```

## Migration Patterns

### Pattern A: Attribute-Only (no forbidden APIs)
For tasks that do pure in-memory transformations with no file I/O, no env vars, no Path.GetFullPath():
```csharp
[MSBuildMultiThreadableTask]
public class MyTask : TaskBase
{
    protected override void ExecuteCore() { /* no global state access */ }
}
```

### Pattern B: Interface-Based (uses forbidden APIs)
For tasks using Path.GetFullPath, File.*, Environment.*, ProcessStartInfo:
```csharp
[MSBuildMultiThreadableTask]
public class MyTask : TaskBase, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; }
    
    protected override void ExecuteCore()
    {
        // CRITICAL: Auto-initialize ProjectDirectory from BuildEngine when not set.
        // Some callers may not explicitly configure TaskEnvironment.ProjectDirectory.
        // In real MSBuild, ProjectFileOfTaskNode is an absolute path to the project file.
        EnsureProjectDirectoryInitialized();

        // Replace: Path.GetFullPath(somePath) for absolutization
        // With:    TaskEnvironment.GetAbsolutePath(somePath)
        
        // Replace: Path.GetFullPath(somePath) for canonicalization (resolving "..", normalizing)
        // With:    TaskEnvironment.GetCanonicalForm(somePath)
        
        // Replace: new FileStream(relativePath, ...)
        // With:    new FileStream(TaskEnvironment.GetAbsolutePath(relativePath), ...)
        
        // Replace: Environment.GetEnvironmentVariable("VAR")
        // With:    TaskEnvironment.GetEnvironmentVariable("VAR")
    }

    private void EnsureProjectDirectoryInitialized()
    {
        if (string.IsNullOrEmpty(TaskEnvironment.ProjectDirectory) && BuildEngine != null)
        {
            string projectFile = BuildEngine.ProjectFileOfTaskNode;
            if (!string.IsNullOrEmpty(projectFile))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? string.Empty;
                TaskEnvironment.ProjectDirectory = dir;
            }
        }
    }
}
```

### Post-Migration Verification Checklist

After completing the migration, verify ALL forbidden APIs have been replaced. **Adding the attribute, interface, and TaskEnvironment property is not sufficient — the actual forbidden API calls in the Execute()/ExecuteCore() method body MUST be replaced.**

Search the migrated file for these patterns and confirm ZERO occurrences remain in the task logic:
1. `Path.GetFullPath(` — allowed ONLY inside `EnsureProjectDirectoryInitialized()` for the `BuildEngine.ProjectFileOfTaskNode` fallback; must be zero occurrences in Execute()/ExecuteCore() body
2. `Environment.CurrentDirectory` — must be zero occurrences
3. `Environment.GetEnvironmentVariable(` — must be zero occurrences
4. `Console.` — must be zero occurrences

If any `Path.GetFullPath()` remains in the Execute() body, the migration is **incomplete** and tests will fail.

**Important**: Do NOT null-check `TaskEnvironment`. MSBuild always provides a `TaskEnvironment` instance to tasks implementing `IMultiThreadableTask` — even in single-threaded mode (where it acts as a no-op passthrough). Use `TaskEnvironment` directly.

### CRITICAL: Defensive ProjectDirectory Initialization

**Always** auto-initialize `TaskEnvironment.ProjectDirectory` from `BuildEngine.ProjectFileOfTaskNode` at the start of `Execute()` (or `ExecuteCore()`) when `ProjectDirectory` is empty. Some test harnesses and callers may not explicitly configure `TaskEnvironment.ProjectDirectory`. Without this fallback, `TaskEnvironment.GetAbsolutePath(relativePath)` returns the raw relative path (since `Path.Combine("", relativePath)` == `relativePath`), causing file operations to resolve against the process CWD instead of the project directory.

```csharp
// Add this at the START of Execute()/ExecuteCore(), BEFORE any path resolution:
if (string.IsNullOrEmpty(TaskEnvironment.ProjectDirectory) && BuildEngine != null)
{
    string projectFile = BuildEngine.ProjectFileOfTaskNode;
    if (!string.IsNullOrEmpty(projectFile))
    {
        TaskEnvironment.ProjectDirectory =
            Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? string.Empty;
    }
}
```

**Why this matters**: In real MSBuild, `ProjectFileOfTaskNode` is always an absolute path (e.g., `C:\src\MyProject\MyProject.csproj`), so `Path.GetDirectoryName` gives the correct project directory. Tests that don't explicitly set `TaskEnvironment` will fall back to resolving relative to the project file location. Without this pattern, the default `TaskEnvironment.ProjectDirectory` is `""` and relative paths resolve to CWD — which breaks when tests use `TestHelper.CreateNonCwdTempDirectory()` to detect CWD-dependent bugs.

## Forbidden API Reference

### Must Replace with TaskEnvironment:
- `Path.GetFullPath(path)` → see **Path.GetFullPath Replacement Decision** below
- `Environment.GetEnvironmentVariable(name)` → `TaskEnvironment.GetEnvironmentVariable(name)`
- `Environment.SetEnvironmentVariable(name, value)` → `TaskEnvironment.SetEnvironmentVariable(name, value)`
- `Environment.CurrentDirectory` → `TaskEnvironment.ProjectDirectory`
- `new ProcessStartInfo(...)` → `TaskEnvironment.GetProcessStartInfo()`

### Path.GetFullPath Replacement Decision (CRITICAL)
Every `Path.GetFullPath()` call MUST be replaced — but the replacement depends on **intent**:

| Original Usage | Intent | Correct Replacement |
|---|---|---|
| `Path.GetFullPath(relativePath)` used to make a relative path absolute before file I/O | Absolutization | `TaskEnvironment.GetAbsolutePath(relativePath)` |
| `Path.GetFullPath(pathWithDotDot)` used to resolve `..` segments, normalize separators, or produce a canonical form | Canonicalization | `TaskEnvironment.GetCanonicalForm(pathWithDotDot)` |
| `Path.GetFullPath(path)` used for both (make absolute AND normalize) | Both | `TaskEnvironment.GetCanonicalForm(path)` |

**How to determine intent**: Look at the variable name (`canonicalPath`, `normalizedPath`, `fullPath`), the log message (e.g., `"Canonical path:"`, `"Normalized:"`), and whether the input contains `..` segments or mixed separators. If a task's **name** contains "Canonical", "Canonicalize", "Normalize", or "FullPath", it is almost certainly a canonicalization pattern.

**Common mistake**: Replacing `Path.GetFullPath()` with only `TaskEnvironment.GetAbsolutePath()` when the task was doing canonicalization. `GetAbsolutePath()` just prepends `ProjectDirectory` — it does NOT resolve `..` segments or normalize path separators. Use `GetCanonicalForm()` when the original code needed path normalization.

**WARNING**: `TaskEnvironment.GetCanonicalForm(path)` is a method directly on `TaskEnvironment`. Do NOT confuse it with `AbsolutePath.GetCanonicalForm()` (which is a method on the `AbsolutePath` struct). Call it as: `TaskEnvironment.GetCanonicalForm(path)`.

### Must Use Absolute Paths:
- `File.Exists(path)` — path must be absolute
- `File.ReadAllText(path)` — path must be absolute
- `File.Create(path)` — path must be absolute
- `new FileStream(path, ...)` — path must be absolute
- `Directory.Exists(path)` — path must be absolute
- `Directory.CreateDirectory(path)` — path must be absolute
- `XDocument.Load(path)` — path must be absolute
- `XDocument.Save(path)` — path must be absolute

### Never Use:
- `Environment.Exit()`, `Environment.FailFast()`
- `Process.GetCurrentProcess().Kill()`
- `Console.*`

## Testing Requirements

### Thread-Safety Test Pattern
Every migrated task needs tests that:
1. **Verify IMultiThreadableTask implementation** (for interface-based tasks)
2. **Verify correct path resolution with non-default project directory** — the test should set TaskEnvironment.ProjectDirectory to a specific directory and verify paths resolve relative to it, NOT the process working directory
3. **Verify the task works correctly** — basic functional test with TaskEnvironment set
4. **Tests must FAIL on an improperly migrated task** — if someone removes the TaskEnvironment usage, the tests should catch it

### Test Template for Interface-Based Tasks
```csharp
public class GivenAMyTaskMultiThreading
{
    [Fact]
    public void ItImplementsIMultiThreadableTask()
    {
        var task = new MyTask();
        task.Should().BeAssignableTo<IMultiThreadableTask>();
    }

    [Fact]
    public void ItHasMSBuildMultiThreadableTaskAttribute()
    {
        typeof(MyTask).Should().BeDecoratedWith<MSBuildMultiThreadableTaskAttribute>();
    }

    [Fact]
    public void ItResolvesRelativePathsViaTaskEnvironment()
    {
        // Create a temp directory to act as a fake project dir
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(projectDir);
        try
        {
            // Set up task with TaskEnvironment pointing to projectDir
            var task = new MyTask();
            task.BuildEngine = new MockBuildEngine();
            task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
            
            // Set relative path inputs
            task.SomePathProperty = "relative/path/file.txt";
            
            // Create the expected file at the project-dir-relative location
            var expectedAbsPath = Path.Combine(projectDir, "relative/path/file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedAbsPath));
            File.WriteAllText(expectedAbsPath, "test");
            
            // Execute and verify it found the file via TaskEnvironment, not CWD
            var result = task.Execute();
            // Assert based on task behavior
        }
        finally
        {
            Directory.Delete(projectDir, true);
        }
    }
}
```

## Generalized Reflection-Based Test Harness

Instead of writing individual test methods for each task, use a reflection-based harness:

```csharp
public static class MigrationTestHarness
{
    /// <summary>
    /// Validates that a migrated task resolves all output paths relative to ProjectDirectory.
    /// Works for any IMultiThreadableTask without task-specific test code.
    /// </summary>
    public static void ValidateOutputPathResolution(ITask task, string projectDir)
    {
        var taskType = task.GetType();
        
        // Verify all Output string properties contain projectDir
        foreach (var prop in taskType.GetProperties()
            .Where(p => p.GetCustomAttribute<OutputAttribute>() != null && p.PropertyType == typeof(string)))
        {
            var value = prop.GetValue(task) as string;
            if (!string.IsNullOrEmpty(value))
                Assert.StartsWith(projectDir, value,
                    $"Output property {prop.Name} should resolve to ProjectDirectory");
        }
        
        // Verify all Output ITaskItem[] properties have items rooted under projectDir
        foreach (var prop in taskType.GetProperties()
            .Where(p => p.GetCustomAttribute<OutputAttribute>() != null && p.PropertyType == typeof(ITaskItem[])))
        {
            var items = prop.GetValue(task) as ITaskItem[];
            if (items != null)
                foreach (var item in items)
                    Assert.StartsWith(projectDir, item.ItemSpec,
                        $"Output item in {prop.Name} should resolve to ProjectDirectory");
        }
    }
    
    /// <summary>
    /// Validates the migrated task preserves the exact public API surface of the original.
    /// </summary>
    public static void ValidatePublicApiPreserved(Type migratedType, string[] expectedProperties)
    {
        var actual = migratedType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(expectedProperties.OrderBy(n => n).ToArray(), actual);
    }
}
```

Use this harness in pipeline-generated tests instead of per-task attribute/interface checks.

## Polyfills Created (Phase 0)

After the polyfill-setup task completes, the following will be available:

### In src/Tasks/Common/ (gated with #if NETFRAMEWORK):
- `IMultiThreadableTask.cs` — the interface with `TaskEnvironment TaskEnvironment { get; set; }`
- `TaskEnvironment.cs` — class with `GetAbsolutePath()`, `GetEnvironmentVariable()`, etc.
- `AbsolutePath.cs` — struct with `Value`, `OriginalValue`, implicit string conversion
- `ITaskEnvironmentDriver.cs` — internal driver interface

### In test project:
- `TaskEnvironmentHelper.cs` — `CreateForTest()` and `CreateForTest(string projectDirectory)` methods

## Build & Test Commands

```bash
# Build the task projects
dotnet build src/Tasks/Microsoft.NET.Build.Tasks/Microsoft.NET.Build.Tasks.csproj
dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks/Microsoft.NET.Build.Extensions.Tasks.csproj

# Run unit tests
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```

## Important Notes

- The project targets both `net472` and `$(SdkTargetFramework)` — polyfills use `#if NETFRAMEWORK`
- `TaskBase` is in namespace `Microsoft.NET.Build.Tasks`, polyfills go in `Microsoft.Build.Framework`
- Tests use `MockBuildEngine` (IBuildEngine4) — set `task.BuildEngine = new MockBuildEngine()`
- Always set `task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest()` in tests for migrated tasks
- **Do NOT null-check `TaskEnvironment`** — MSBuild always provides it to `IMultiThreadableTask` implementations, even in single-threaded mode (where it acts as a passthrough). Use `TaskEnvironment` directly without guards.
- **Always auto-initialize `TaskEnvironment.ProjectDirectory`** from `BuildEngine.ProjectFileOfTaskNode` at the start of `Execute()` when `ProjectDirectory` is empty. This ensures the task works even when callers don't explicitly configure `TaskEnvironment`. See "Defensive ProjectDirectory Initialization" section above.
- Trace ALL path strings through helper methods to catch indirect file API usage
- `GetAbsolutePath()` throws on null/empty — handle in batch operations
- The real MSBuild `AbsolutePath` requires fully-qualified paths (with drive letter on Windows). Test paths must be fully qualified — use `Path.GetFullPath()` on synthetic test paths before passing to `TaskEnvironmentHelper.CreateForTest()`.
- For `Path.GetFullPath()` used for canonicalization, use `TaskEnvironment.GetCanonicalForm(path)` (NOT `GetAbsolutePath` — that does not normalize `..` segments)
- **Every** `Path.GetFullPath()` in Execute() body must be replaced — do NOT leave any behind after adding the interface/attribute
