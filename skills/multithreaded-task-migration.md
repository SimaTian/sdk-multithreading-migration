# Skill: MSBuild Multithreaded Task Migration

## Context

This skill covers migrating MSBuild tasks in the `SimaTian/sdk` repository (branch `main`) to support multithreaded execution. The repository is at https://github.com/SimaTian/sdk/tree/main.

This skill is the **canonical home** for shared rules referenced by `analyze-and-migrate-template.md`, `interface-migration-template.md`, and `stress-testing-migrations.md`. Where those skills mention a rule that lives here, they cross-link rather than duplicating the text.

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
│   └── Resources/Strings.resx           # Localized strings
├── Microsoft.NET.Build.Tasks/           # Main task library
│   ├── Microsoft.NET.Build.Tasks.csproj # Targets: net472 + $(SdkTargetFramework)
│   └── *.cs                             # Task implementations
├── Microsoft.NET.Build.Tasks.UnitTests/ # Unit tests (xUnit + FluentAssertions/AwesomeAssertions)
│   ├── Mocks/MockBuildEngine.cs         # IBuildEngine4 mock for tests
│   └── Given*.cs                        # Test files
├── Microsoft.NET.Build.Extensions.Tasks/
└── Microsoft.NET.Build.Extensions.Tasks.UnitTests/
```

## Key Classes

### TaskBase (src/Tasks/Common/TaskBase.cs)
```csharp
public abstract class TaskBase : Task
{
    internal new Logger Log { get; }
    public override bool Execute();   // catches BuildErrorException, logs telemetry
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
}
```

## Migration Patterns

### Pattern A: Attribute-Only (pure in-memory tasks)

Pattern A means **only** the attribute. Use it only after confirming the full `Execute()` path (including helpers) has no filesystem access, environment access, CWD access, process-global state, `IBuildEngine` callbacks, or shared mutable caches.

```csharp
[MSBuildMultiThreadableTask]
public class MyTask : TaskBase
{
    protected override void ExecuteCore() { /* pure in-memory only */ }
}
```

**Do NOT** add `IMultiThreadableTask`, a `TaskEnvironment` property, a NETFRAMEWORK fallback getter, or a per-task `GivenA<Task>MultiThreading` test file for Pattern A. Those are over-migration for a pure task. If `[MSBuildMultiThreadableTask]` is already present and the forbidden-API scan is clean, Pattern A is already complete: make zero code changes and only run regression validation.

**Instant Pattern A disqualifiers** — any of the following on the reachable execution path moves the task to Pattern B (or blocks the migration):

- any reachable `File.*`, `Directory.*`, `ZipFile.*`, `XDocument.Load(path)`, `XDocument.Save(path)`
- `Path.GetFullPath`, `Path.IsPathRooted`-gated I/O
- `Environment.*`, `ProcessStartInfo`, `Process.Start`
- `Console.*`, `Trace.*` backed by `Console`, or any logging helper with mutable static state
- `IBuildEngine` / `IBuildEngine4`+ callbacks (`LogTelemetry`, `Log*Event`, `BuildProjectFile`, `GetRegisteredTaskObject`/`RegisterTaskObject`, `ContinueOnError` state, `ProjectFileOfTaskNode`)
- `RegisteredTaskObject` shared cache use, static mutable collections/caches, `static readonly` env-var fields
- helpers whose name/signature suggests path I/O: `*Parser.Parse(path)`, `*Loader.Load(path)`, `*Resolver.Resolve(path)`, `*Utilities.*`

If the task writes/deletes/creates files or archives, Pattern A is invalid unless the PR proves all concurrent invocations have distinct output paths by inspecting `.targets`/`.props` call sites. When in doubt, omit the attribute or migrate as Pattern B.

> Reviewer-deletion evidence: PR #53954 (`Delete tests, remove unnecessary interface on task`), PR #53956 (`Remove interface from ShowPreviewMessage, remove tests`), PR #53949 ("we do not need tests when we just assert that attribute was added").

### Pattern B: Interface-Based (uses forbidden APIs)

```csharp
[MSBuildMultiThreadableTask]
public class MyTask : TaskBase, IMultiThreadableTask
{
    /// <inheritdoc/>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    protected override void ExecuteCore()
    {
        // Replace: Path.GetFullPath(somePath)
        // With:    TaskEnvironment.GetAbsolutePath(somePath)

        // Replace: new FileStream(relativePath, ...)
        // With:    new FileStream(TaskEnvironment.GetAbsolutePath(relativePath).Value, ...)

        // Replace: Environment.GetEnvironmentVariable("VAR")
        // With:    TaskEnvironment.GetEnvironmentVariable("VAR")
    }
}
```

#### Canonical TaskEnvironment property

Use the single cross-targeted line above — no `#if NETFRAMEWORK`, no backing field, no `= null!` suppressor. `TaskEnvironment.Fallback` is a CWD-based passthrough that is safe when MSBuild has not yet injected a real instance (legacy single-threaded callers, or unit tests that forget to assign the property). It does not snapshot CWD at construction — every call resolves against the live process CWD. `/// <inheritdoc/>` surfaces the `IMultiThreadableTask` XML doc on the implementation.

> Verify `TaskEnvironment.Fallback` exists in your target branch. If not, fall back to a simple auto-property and update tests to assign `TaskEnvironmentHelper.CreateForTest(...)` — but do NOT introduce a CWD-seeded lazy getter.

**Never silently fall back to process-global state:**

```csharp
// ❌ WRONG: null-conditional changes semantics and can enable features accidentally
private bool AllowCacheLookup => TaskEnvironment?.GetEnvironmentVariable("ALLOW_CACHE") != "0";

// ❌ WRONG: corrupts AbsolutePath.OriginalValue and reintroduces CWD-relative behavior
AbsolutePath p = TaskEnvironment?.GetAbsolutePath(Input) ?? new AbsolutePath(Path.GetFullPath(Input));

// ❌ WRONG: snapshots process CWD/env vars in a task getter
get => _taskEnvironment ??= new TaskEnvironment(new ProcessTaskEnvironmentDriver(Directory.GetCurrentDirectory()));

// ❌ WRONG: chained .Value on nullable struct — NREs before ?? fires on .NET Core
string p = TaskEnvironment?.GetAbsolutePath(x).Value ?? Path.GetFullPath(x);
```

```csharp
// ✅ GOOD
bool allowCacheLookup = TaskEnvironment.GetEnvironmentVariable("ALLOW_CACHE") != "0";
AbsolutePath input = TaskEnvironment.GetAbsolutePath(Input);
```

**Pre-merge grep check** for the migration PR:

```powershell
rg 'TaskEnvironment\?\.'                  # nullable callsites
rg '\?\?\s*Path\.GetFullPath'             # CWD-fallback expressions
rg 'TaskEnvironment\?\s+TaskEnvironment'  # nullable property
```

All three must return zero hits. Reject AI-reviewer suggestions to add null guards: with `= TaskEnvironment.Fallback`, the property is never null at runtime; defensive guards are dead code that mask real bugs.

## AbsolutePath Usage Pattern

**Hard boundary rule:**
- Use `AbsolutePath.Value` only at filesystem/process boundaries: `File.*`, `Directory.*`, `FileStream`, `XDocument.Load(stream)`, external APIs that require an absolute filesystem path.
- Use `AbsolutePath.OriginalValue` (or the original input property) for anything that escapes the task: `FilesWritten`, output `ItemSpec`, output item metadata, task output properties, log messages that echo user input.
- Do not cast `AbsolutePath` to `string` early. Keep the typed value until the exact boundary and write `.Value` or `.OriginalValue` explicitly.
- Type locals as `AbsolutePath`, not `string` — the type signals "this has been routed through `TaskEnvironment` and is safe for file I/O", and the implicit string conversion fires at the use site.

```csharp
// ✅ GOOD
AbsolutePath assetsFilePath = TaskEnvironment.GetAbsolutePath(AssetsFilePath);
if (File.Exists(assetsFilePath)) { ... }       // implicit AbsolutePath -> string

// ❌ BAD: strips type-level proof of absolutization, discards OriginalValue
string assetsFilePath = (string)TaskEnvironment.GetAbsolutePath(AssetsFilePath);
```

### Optional path → output metadata recipe

```csharp
AbsolutePath? resolvedPackDirectory = null;
if (!string.IsNullOrEmpty(originalPackDirectory))
    resolvedPackDirectory = TaskEnvironment.GetAbsolutePath(Path.Combine(originalPackDirectory, hostRelativePathInPackage));

if (resolvedPackDirectory.HasValue && Directory.Exists(resolvedPackDirectory.Value.Value))
{
    UseForIo(resolvedPackDirectory.Value.Value);
    item.SetMetadata(MetadataKeys.PackageDirectory, resolvedPackDirectory.Value.OriginalValue);
    item.SetMetadata(MetadataKeys.Path,
        Path.Combine(resolvedPackDirectory.Value.OriginalValue, hostRelativePathInPackage));
}
```

Disambiguation: outer `.Value` unwraps the nullable struct; inner `.Value` is the absolute string for I/O; `.OriginalValue` is the caller's input form for output metadata. Do NOT shadow these with parallel `string originalX` / `string resolvedXValue` locals — `AbsolutePath` already carries both forms.

### Reserved metadata ban

**Never** call `SetMetadata` for any of these MSBuild reserved keys:

`FullPath`, `RootDir`, `Filename`, `Extension`, `RelativeDir`, `Directory`, `RecursiveDir`, `Identity`, `ModifiedTime`, `CreatedTime`, `AccessedTime`.

`ITaskItem.SetMetadata("FullPath", ...)` throws on real `TaskItem` even if mocks accept it. To expose an absolute output, set the output item `ItemSpec` to the absolute path or let MSBuild compute `%(FullPath)` from a rooted `ItemSpec`.

### Why null fallbacks are dangerous

`new AbsolutePath(Path.GetFullPath(input))` stores the absolute string as `OriginalValue`. Any later `FilesWritten.Add(new TaskItem(abs.OriginalValue))` will emit an absolute path even if the user supplied a relative path.

## Path Conversion Edge Cases

### `Path.GetFullPath` migration decision

1. If the old call only rooted a relative path → `TaskEnvironment.GetAbsolutePath(path)`; keep the result typed as `AbsolutePath`.
2. If the old call normalized `..` segments/separators or produced a canonical string that is externally observed → `Path.GetFullPath(TaskEnvironment.GetAbsolutePath(path).Value)`.
3. When combining a relative segment with an `AbsolutePath` base, prefer `new AbsolutePath(relativeSegment, absoluteBase)`; if `..` normalization is needed, wrap the result: `Path.GetFullPath(new AbsolutePath(relativeSegment, absoluteBase))`.
4. Do **not** reference `AbsolutePath.GetCanonicalForm()` in cross-TFM code — it only existed in the NETFRAMEWORK polyfill.

### Preserve null/empty path semantics

`TaskEnvironment.GetAbsolutePath(null)` and `GetAbsolutePath("")` throw. Before migrating, determine the old behavior:

- If null/empty meant "use CWD" via `Path.GetFullPath(string.IsNullOrEmpty(x) ? Environment.CurrentDirectory : x)`, map it to `TaskEnvironment.ProjectDirectory`.
- If null/empty meant "skip optional work", keep the early return/skip before calling `GetAbsolutePath`.
- If the downstream library historically validates whitespace/null sentinels, pass the sentinel through unchanged:
  ```csharp
  string p = string.IsNullOrWhiteSpace(x) ? x : TaskEnvironment.GetAbsolutePath(x).Value;
  ```

### Windows rooted-but-not-fully-qualified paths

`Path.IsPathRooted()` is not enough. On Windows, paths like `C:foo` or `\publish\bundle` may be rooted/drive-relative but are not fully qualified and `AbsolutePath` rejects them with `ArgumentException`. Tasks that accept user-supplied `OutputDir`/`PublishDir` strings must reject or re-root these forms against `TaskEnvironment.ProjectDirectory` before constructing `AbsolutePath`. Test paths passed to `TaskEnvironmentHelper.CreateForTest(...)` must be fully qualified (`Path.GetFullPath(...)` is the safest setup helper).

## Forbidden API Reference

### Must replace with TaskEnvironment

- `Path.GetFullPath(path)` → `TaskEnvironment.GetAbsolutePath(path)` (see decision above)
- `Environment.GetEnvironmentVariable(name)` → `TaskEnvironment.GetEnvironmentVariable(name)`
- `Environment.SetEnvironmentVariable(name, value)` → `TaskEnvironment.SetEnvironmentVariable(name, value)`
- `Environment.CurrentDirectory` / `Directory.GetCurrentDirectory()` → `TaskEnvironment.ProjectDirectory`
- `new ProcessStartInfo(...)` → `TaskEnvironment.GetProcessStartInfo()`

### Must use absolute paths

- `File.*` (`Exists`, `ReadAllText`, `Create`, `WriteAllText`, `Open`, `Delete`, `Copy`, `Move`)
- `new FileStream(...)`, `new StreamReader(...)`, `new StreamWriter(...)`
- `Directory.Exists`, `Directory.CreateDirectory`, `Directory.Delete`
- `XDocument.Load(path)`, `XDocument.Save(path)` — additionally, prefer the FileShare-safe stream form below

### Never use

- `Environment.Exit()`, `Environment.FailFast()`, `Process.GetCurrentProcess().Kill()`
- `Console.*`, `Trace.*` backed by `Console`, or logging helpers with mutable static state — `TaskEnvironment` cannot intercept these. MSBuild event logging through the task's normal `Log`/`BuildEngine` surface is the only safe output channel.

### Transitive forbidden APIs (external libraries AND internal helpers)

Forbidden API usage can hide inside any method on the task's execution path — external libraries, runtime assemblies, `Common/` helpers, parser/loader utilities, value-object constructors, cache classes, and static helper methods.

For every non-trivial call from `Execute()`/`ExecuteCore`, walk **1–2 hops** and classify each path/env/CWD/console access as:

- **migrate**: add a `TaskEnvironment`-aware overload/delegate and route through it;
- **absolutize-at-caller**: pass an `AbsolutePath.Value` into the helper because the helper only needs a filesystem path;
- **postpone/block**: do not mark the task multi-threadable until the shared/upstream violation is fixed.

**A TODO comment is NOT sufficient for a known transitive violation on the main execution path.**

Known examples:
- `StoreArtifactParser.Parse(manifestFile)` — opens/parses a manifest from disk; not Pattern A-safe (PR #52554).
- `PublishMutationUtilities.ChangeEntryPointLibraryName(...)` — internal helper with file access (PR #52555).
- `DotNetReferenceAssembliesPathResolver.Resolve()` — reads `DOTNET_REFERENCE_ASSEMBLIES_PATH` and probes directories outside `TaskEnvironment` (PR #52936).
- `CliFolderPathCalculatorCore.GetDotnetUserProfileFolderPath()` — reads `DOTNET_CLI_HOME` via process environment.
- `Microsoft.NET.HostModel.Bundle.Trace` — calls `Console.WriteLine` / `Console.Error.WriteLine`; `TaskEnvironment` cannot intercept console output. Fix upstream with a logging callback routed to MSBuild logging, or block the migration (PR #53949).

#### Delegate injection for shared helpers

When a shared helper holds paths/env-var readers but cannot depend on `TaskEnvironment`, inject a resolver/reader delegate. Migrated tasks pass `p => TaskEnvironment.GetAbsolutePath(p).Value`; un-migrated siblings pass `p => p`. For env-var helpers, prefer `Func<string, string?> getEnvironmentVariable` with a default constructor preserving old behavior for non-MSBuild callers.

```csharp
public ConflictItem(ITaskItem originalItem, ConflictItemType itemType,
                    Func<string, string>? pathResolver = null) { ... }
```

#### Injection completeness audit

When a class is refactored from static to instance-based with delegate/interface injection, **every** internal code path must use the injected dependency. A single leaked static call defeats the entire refactoring.

1. List ALL methods in the refactored class.
2. For each method, trace every env var read, filesystem access, and path resolution.
3. Verify EACH one goes through the injected delegate/`TaskEnvironment` — including calls to external library methods that internally read env vars or access the filesystem.
4. A leaked static call is a **bug**, not a TODO — it must be fixed before merge.

Real example: `FrameworkReferenceResolver` was refactored to accept `Func<string, string>` for env var injection. The `ProgramFiles` reads correctly used the delegate, but a sibling line still called `DotNetReferenceAssembliesPathResolver.Resolve()` — a static library method that uses process-global `Environment.GetEnvironmentVariable("DOTNET_REFERENCE_ASSEMBLIES_PATH")`, completely bypassing the delegate (PR #52936).

## Shared State and Cache Audit

Before declaring a task multi-threadable, audit both the task and shared `src/Tasks/Common/**` helpers for process-wide mutable state:

```powershell
rg "static.*(Dictionary|HashSet|List<|Cache|Environment\.GetEnvironmentVariable|RegisteredTaskObject|RegisterTaskObject|GetRegisteredTaskObject)" src\Tasks
```

Rules:

1. Static mutable collections (`Dictionary`, `HashSet`, `List`) are data races. Convert caches to `ConcurrentDictionary`, immutable collections, or lock-protected accessors before any concurrent caller is enabled. (PR #53942 — `s_versionCache` → `ConcurrentDictionary`.)
2. `static readonly` fields initialized from `Environment.GetEnvironmentVariable(...)` are forbidden. They freeze the process environment for the AppDomain and are shared by all task instances. Read via `TaskEnvironment.GetEnvironmentVariable(...)` inside `ExecuteCore`, cache into a local once, and pass the local to helpers. (PR #53122, PR #53944 — `s_allowCacheLookup`.)
3. Any `GetRegisteredTaskObject` / `RegisterTaskObject` check-then-register pair used by an `IMultiThreadableTask` must be protected by a per-cache-key lock.
4. Cache keys must include every input that affects outputs: resolved absolute filesystem path, original relative path/metadata that is emitted later, relevant env-var toggle values, and output-affecting item metadata.
5. After a cache hit, return immediately. Falling through to the load/register path defeats the cache, repeats file I/O, and can duplicate outputs.

## Where to Absolutize (Point-of-Use Patterns)

- **Inputs consumed by multiple helpers or echoed as outputs**: resolve once near the top of `ExecuteCore` and pass `AbsolutePath` to helpers.
- **Inputs consumed by exactly one helper**: resolve inside that helper at point-of-use to avoid leaking absolute strings into unrelated logic.
- **Recursive helpers**: absolutize the root once, pass the absolute root as an explicit parameter, keep child segments relative inside recursion.
- **Derived path expressions**: wrap the OUTERMOST complete path expression in `GetAbsolutePath`, not an inner component only.
- **`ITaskItem[]` inputs**: treat `item.ItemSpec` and path-like metadata as relative path candidates when forwarded to file I/O or external constructors.
- **`ToolTask` subclasses → audit ALL override points** (every one is a common miss):
  - `ValidateParameters()` — every `File.Exists`/`Directory.Exists` uses an absolutized path.
  - `GenerateCommandLineCommands()` — every emitted path argument is absolutized before joining the command line.
  - `GenerateResponseFileCommands()` — every emitted path argument is absolutized.
  - `GenerateFullPathToTool()` — returns `TaskEnvironment.GetAbsolutePath(ToolName)`. **Commonly missed** — the return value flows to the spawned process loader, not visible task logic (PR #53121 follow-up commit `b3d3fc8`).
  - `ExecuteTool()` overrides — any `Directory.CreateDirectory` on output paths uses absolute paths.
  - Private helpers (`GenerateCrossgenResponseFile`, `GetAssemblyReferencesCommands`, etc.) — recurse the audit.

### Concurrent-read-safe file I/O

For migrated tasks reading files that other tasks may read concurrently, prefer explicit streams instead of `XDocument.Load(absolutePath)` (which holds an exclusive lock):

```csharp
using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
                                  FileShare.Read | FileShare.Delete);
XDocument document = XDocument.Load(stream);
```

## Behavioral Testing

Every Pattern B migrated task needs at least one focused behavioral regression test that fails if the task resolves paths/env vars through process-global state instead of `TaskEnvironment`.

**Required properties of the test:**

1. Set `task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)` **unconditionally** on all TFMs.
2. Make `projectDir` distinct from `Directory.GetCurrentDirectory()`; if the test mutates CWD, use `[Collection(...)]` with a `[CollectionDefinition(..., DisableParallelization = true)]` and restore CWD in `finally`/`Dispose`.
3. Feed **relative** `ItemSpec`/`HintPath`/path-property inputs. Passing absolute paths makes decoy-CWD tests a no-op.
4. Create required files **only** under `projectDir` (or create different payloads under `projectDir` and decoy CWD when file content matters).
5. Assert observable outputs: files read/written, `FilesWritten`, output `ITaskItem` metadata, env-var-dependent cache behavior. Do **not** guard the main assertion behind `if`; first assert the output/token exists, then assert its value.
6. Provide non-empty inputs that force execution of the migrated code path; empty arrays that return before `GetAbsolutePath` are invalid migration tests.

**Do NOT add per-task tests that only assert:**
- the class implements `IMultiThreadableTask`
- `[MSBuildMultiThreadableTask]` is present
- a `TaskEnvironment` property exists
- reflection can set the property

There is no narrow exception for reflection-based attribute/interface presence tests. Reflection does not change the calculus — these tests verify decoration, not behavior, and pass trivially once the attribute is added.

### Test setup conventions (canonical home)

Direct assignment via initializer or interface cast — never reflection when the property is public:

```csharp
var task = new MyTask
{
    BuildEngine = new MockBuildEngine(),
    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)
};
// or, for generic helpers:
((IMultiThreadableTask)task).TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
```

- Do not hide `TaskEnvironment` setup behind `#if NETFRAMEWORK`; never assign `task.TaskEnvironment = null!` in `#else`. .NET Core test runs must exercise the same `TaskEnvironment` path.
- `projectDir` passed to `CreateForTest` must be fully qualified (`Path.GetFullPath(...)`).
- Decoy-CWD tests must pass **relative** `ItemSpec`/path inputs, not absolute temp paths.
- Shared test factories that pre-configure tasks must assign `TaskEnvironment` ONCE — either in the factory OR at the call site, never both. Double-assignment masks bugs where per-test customization is silently overwritten.

### Test template for interface-based tasks

```csharp
[Collection("CWD-Dependent")]
public class GivenAMyTaskMultiThreading
{
    [Fact]
    public void ItResolvesRelativePathsViaTaskEnvironment()
    {
        var projectDir = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), $"mytask-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var task = new MyTask
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                SomePathProperty = "relative/path/file.txt",
            };

            var expectedAbsPath = Path.Combine(projectDir, "relative/path/file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedAbsPath)!);
            File.WriteAllText(expectedAbsPath, "test");

            task.Execute().Should().BeTrue();
            // Assert observable outputs...
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
```

## TaskEnvironment Lifecycle

When MSBuild detects that a task implements `IMultiThreadableTask`, it:
1. Creates a fully initialized `TaskEnvironment` instance (with `ProjectDirectory` set from the project file)
2. Assigns it to the task's `TaskEnvironment` property via the setter
3. Then calls `Execute()`

The `= TaskEnvironment.Fallback` initializer covers the rare case where MSBuild has not injected (legacy single-threaded callers, unit tests).

**Do NOT add any of these patterns in task source code:**

```csharp
// ❌ WRONG — do not self-initialize from BuildEngine
if (string.IsNullOrEmpty(TaskEnvironment.ProjectDirectory) && BuildEngine != null)
    TaskEnvironment.ProjectDirectory = Path.GetDirectoryName(BuildEngine.ProjectFileOfTaskNode);

// ❌ WRONG — do not add an EnsureProjectDirectoryInitialized method
private void EnsureProjectDirectoryInitialized() { ... }
```

For unit tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)` to set `TaskEnvironment` manually.

## Polyfills (Phase 0 — now upstream)

The polyfill foundation (`IMultiThreadableTask`, `TaskEnvironment`, `AbsolutePath`, `MSBuildMultiThreadableTaskAttribute`, `ITaskEnvironmentDriver`, `ProcessTaskEnvironmentDriver`) is provided upstream. Polyfill *authoring* guidance (visibility, normalization semantics) is no longer in this skill.

What remains relevant for migration authors:

- `Microsoft.NET.Build.Tasks.csproj` still has `<TargetFrameworks>$(SdkTargetFramework);net472</TargetFrameworks>`. Net472 driver/process traps (covered in `interface-migration-template.md`) still apply: `ProcessStartInfo.EnvironmentVariables` (not `.Environment`); `UseShellExecute = false` unconditionally; `Path.IsPathFullyQualified` unavailable on net472.
- `TaskEnvironmentHelper.CreateForTest()` and `CreateForTest(string projectDirectory)` live in the test project.

## Build & Test Commands

```bash
dotnet build src/Tasks/Microsoft.NET.Build.Tasks/Microsoft.NET.Build.Tasks.csproj
dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks/Microsoft.NET.Build.Extensions.Tasks.csproj

dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```

## Important Notes

- The project targets both `net472` and `$(SdkTargetFramework)`.
- `TaskBase` is in namespace `Microsoft.NET.Build.Tasks`; framework polyfills are in `Microsoft.Build.Framework`.
- Tests use `MockBuildEngine` (IBuildEngine4) — set `task.BuildEngine = new MockBuildEngine()`.
- Always set `task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)` in tests for migrated tasks.
- Do **NOT** null-check `TaskEnvironment` — use it directly with the `= TaskEnvironment.Fallback` initializer.
- Do **NOT** add defensive `ProjectDirectory` self-initialization in task source code.
- Trace ALL path strings through helper methods to catch indirect file API usage; a TODO is not enough.
- `GetAbsolutePath()` throws on null/empty — handle in batch operations and preserve sentinels for downstream libraries.
- The real MSBuild `AbsolutePath` requires fully-qualified paths (drive letter on Windows). Test paths must be fully qualified — use `Path.GetFullPath()` on synthetic test paths before passing to `TaskEnvironmentHelper.CreateForTest()`.
