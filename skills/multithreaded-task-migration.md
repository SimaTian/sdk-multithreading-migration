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
- Trace ALL path strings through helper methods to catch indirect file API usage
- `GetAbsolutePath()` throws on null/empty — handle in batch operations
- The real MSBuild `AbsolutePath` requires fully-qualified paths (with drive letter on Windows). Test paths must be fully qualified — use `Path.GetFullPath()` on synthetic test paths before passing to `TaskEnvironmentHelper.CreateForTest()`.
- For `Path.GetFullPath()` used for canonicalization, use `TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm()`
