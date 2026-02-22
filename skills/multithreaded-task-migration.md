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
        // Replace: Path.GetFullPath(somePath)
        // With:    TaskEnvironment.GetAbsolutePath(somePath)
        
        // Replace: new FileStream(relativePath, ...)
        // With:    new FileStream(TaskEnvironment.GetAbsolutePath(relativePath), ...)
        
        // Replace: Environment.GetEnvironmentVariable("VAR")
        // With:    TaskEnvironment.GetEnvironmentVariable("VAR")
    }
}
```

**Important**: Do NOT null-check `TaskEnvironment`. MSBuild always provides a `TaskEnvironment` instance to tasks implementing `IMultiThreadableTask` — even in single-threaded mode (where it acts as a no-op passthrough). Use `TaskEnvironment` directly.

**Important**: Do NOT add defensive `ProjectDirectory` self-initialization code in the task. MSBuild sets `TaskEnvironment` (including `ProjectDirectory`) via the property setter before calling `Execute()`. The task only needs the simple auto-property `public TaskEnvironment TaskEnvironment { get; set; }`. For unit tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)` to provide a properly initialized `TaskEnvironment` manually. See "TaskEnvironment Lifecycle" section below.

## TaskEnvironment Lifecycle

**MSBuild handles `TaskEnvironment` initialization — the task does NOT need to self-initialize.**

When MSBuild detects that a task implements `IMultiThreadableTask`, it:
1. Creates a fully initialized `TaskEnvironment` instance (with `ProjectDirectory` set from the project file)
2. Assigns it to the task's `TaskEnvironment` property via the setter
3. Then calls `Execute()`

This means the task source code should be a simple auto-property with no initialization logic:
```csharp
public TaskEnvironment TaskEnvironment { get; set; }
```

**Do NOT add any of these patterns in task source code:**
```csharp
// ❌ WRONG — do not self-initialize from BuildEngine
if (string.IsNullOrEmpty(TaskEnvironment.ProjectDirectory) && BuildEngine != null)
{
    TaskEnvironment.ProjectDirectory = Path.GetDirectoryName(BuildEngine.ProjectFileOfTaskNode);
}

// ❌ WRONG — do not add an EnsureProjectDirectoryInitialized method
private void EnsureProjectDirectoryInitialized() { ... }
```

**For unit tests**, MSBuild is not involved, so you must set `TaskEnvironment` manually:
```csharp
task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
```

This is confirmed by all Groups 1–4 tasks (GenerateDepsFile, GenerateBundle, ResolveAppHosts, CreateAppHost, ResolvePackageDependencies, etc.) which all use simple `{ get; set; }` with no self-initialization.

## Forbidden API Reference

### Must Replace with TaskEnvironment:
- `Path.GetFullPath(path)` → `TaskEnvironment.GetAbsolutePath(path)` (or `.GetCanonicalForm()` if canonicalization was the purpose)
- `Environment.GetEnvironmentVariable(name)` → `TaskEnvironment.GetEnvironmentVariable(name)`
- `Environment.SetEnvironmentVariable(name, value)` → `TaskEnvironment.SetEnvironmentVariable(name, value)`
- `Environment.CurrentDirectory` → `TaskEnvironment.ProjectDirectory`
- `new ProcessStartInfo(...)` → `TaskEnvironment.GetProcessStartInfo()`

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

### Transitive Violations (External Library Calls)
Forbidden API usage can hide inside external library methods that the task calls. These are the hardest violations to detect because they don't appear in the task's source code — the library internally calls `Environment.GetEnvironmentVariable()`, `Path.GetFullPath()`, `Directory.Exists()`, etc. without any knowledge of `TaskEnvironment`.

**During analysis, for every non-trivial external method call, ask: does this library method internally access environment variables, the filesystem, or the CWD?** If yes, it's a transitive violation.

Known examples:
- `DotNetReferenceAssembliesPathResolver.Resolve()` (`Microsoft.Extensions.DependencyModel`) — reads `DOTNET_REFERENCE_ASSEMBLIES_PATH` via `Environment.GetEnvironmentVariable()` and probes directories with `Directory.Exists()`, all bypassing `TaskEnvironment`

**Fix**: Replace the library call with inline code that uses `TaskEnvironment`, or document it as a known limitation with a TODO comment linking to the library source.

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
- **Do NOT add defensive `ProjectDirectory` self-initialization** in the task source code (e.g., reading `BuildEngine.ProjectFileOfTaskNode` to set `TaskEnvironment.ProjectDirectory`). MSBuild creates a fully initialized `TaskEnvironment` — with `ProjectDirectory` already set from the project file — and assigns it to the property before calling `Execute()`. The task only needs `public TaskEnvironment TaskEnvironment { get; set; }`. For tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)` to set it manually.
- Trace ALL path strings through helper methods to catch indirect file API usage
- `GetAbsolutePath()` throws on null/empty — handle in batch operations
- The real MSBuild `AbsolutePath` requires fully-qualified paths (with drive letter on Windows). Test paths must be fully qualified — use `Path.GetFullPath()` on synthetic test paths before passing to `TaskEnvironmentHelper.CreateForTest()`.
- For `Path.GetFullPath()` used for canonicalization, use `TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm()`
