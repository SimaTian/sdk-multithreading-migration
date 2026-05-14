# Skill: Interface-Based Task Migration Template (TDD Approach)

## Prerequisite

Complete the Step 0 decision gate from [`analyze-and-migrate-template.md` § Step 0](./analyze-and-migrate-template.md#step-0--pre-analysis-is-the-task-already-enlightened). Do NOT proceed if the task is already enlightened or has zero forbidden-API calls — apply Pattern A (attribute only) instead.

This skill defers to [`multithreaded-task-migration.md`](./multithreaded-task-migration.md) for the canonical `TaskEnvironment` property declaration, the `where to absolutize` checklist, the `AbsolutePath.OriginalValue` recipe, drive-relative path / null-sentinel handling, and the test setup conventions. It owns this skill's own concerns: stub workflow, polyfill visibility, decoy-CWD test design, net472 driver traps, and minimal-suite test guidance.

## Core Principle: Tests First, Migration Second

This migration follows a strict workflow:

1. **Read & analyze** the task for forbidden APIs (cross-ref `analyze-and-migrate-template.md` Step 2 + Step 2d transitive audit)
2. **Write behavioral tests** — one focused decoy-CWD / output-relativity regression
3. **Apply stub migration** — attribute + interface + `TaskEnvironment` property only, NO logic changes
4. **Verify tests FAIL for behavioral reasons** — proves tests are not no-ops
5. **Complete the migration** — absolutize paths via `TaskEnvironment`
6. **Verify the tests pass**
7. **Verify existing tests still pass** — no regressions; audit ALL legacy `new <TaskName>(` sites

## Standard Migration Steps

### Step 1: Clone & Prepare

```bash
git clone https://github.com/SimaTian/sdk.git && cd sdk && git checkout main
```

### Step 2: Read & Analyze the Task File

Identify ALL forbidden API usage in the task body and helpers. The full forbidden-API table and Step 2d transitive call-graph procedure live in [`analyze-and-migrate-template.md` § Step 2](./analyze-and-migrate-template.md#step-2-analyze-for-forbidden-apis).

#### Generalized transitive-audit rule (interface-migration specific)

Audit every external library entry point **by name**, not by `File.*` grep. Two heuristic flags:

- **(a)** the helper name contains `Parse`/`Load`/`Resolve`/`Read`/`Write` AND accepts a path string → presume transitive file I/O.
- **(b)** any output channel other than `Log.LogXxx` (`Console`, `Trace`, `Debug`, file logs) → presume non-task communication and therefore forbidden.

(Examples: `StoreArtifactParser.Parse(manifestFile)` from PR #52554; `HostModel.Bundle.Trace` writing to `Console.WriteLine`/`Console.Error.WriteLine` from PR #53949.)

### Step 3: Write Failing Tests FIRST (before any code changes)

Create a test file `GivenA<TheTask>MultiThreading.cs` in the UnitTests project.

**Do NOT write tests for attribute or interface presence** — including reflection-based variants. See `analyze-and-migrate-template.md` Test Design Rule 1 (strong ban, no exceptions).

#### Minimal committed suite

Per-task committed tests should be limited to ONE focused behavioral regression that fails against a stub migration — typically the output-relativity / decoy-CWD test. Treat the others as optional or local-only validation:

- **Multi-process vs multi-threaded parity test** — useful while developing; reviewers often delete it as redundant with the decoy-CWD test once the migration is correct.
- **Concurrent execution / stress test** — DO NOT commit. Run high-parallelism checks locally (see [`stress-testing-migrations.md`](./stress-testing-migrations.md)) when shared-state risk exists. Reviewers consistently remove committed concurrent stress tests as low-value: PR #53949 commit `93fb85ce` removed a 64-thread `CountdownEvent`+`ManualResetEventSlim` test; PR #53955 commit `7c4e4511` (@AR-May) removed `ParallelExecution_ResolvesRelativePacksWithSharedRuntimeGraphCache` with the message "Remove tests with low value."
- **Reflection-based interface/attribute presence assertions** — DO NOT commit (see Pattern B test setup section below).
- **Property-setter "ExistsAndCanBeSet" tests** — only commit if they actually exercise the setter (see "Setter test rule" below).

#### CWD-mutating tests need [Collection] + [CollectionDefinition(DisableParallelization=true)] + IDisposable fixture

**MANDATORY — xUnit isolation for CWD-mutating tests.** Any test class that calls `Directory.SetCurrentDirectory` (directly or via a helper such as `TaskTestEnvironment`) MUST satisfy ALL of the following:

1. Declare a single sealed `CollectionDefinition` class somewhere in the test project (once per project):
   ```csharp
   [CollectionDefinition(CwdSensitiveCollection.Name, DisableParallelization = true)]
   public sealed class CwdSensitiveCollection { public const string Name = "CwdSensitive"; }
   ```
   Without `DisableParallelization = true`, the `[Collection]` attribute is a no-op for parallelism control and the race persists.

2. Decorate every CWD-mutating test class with `[Collection(CwdSensitiveCollection.Name)]`.

3. Prefer the `IDisposable` fixture pattern over per-test `try/finally`:
   ```csharp
   [Collection(CwdSensitiveCollection.Name)]
   public class GivenATheTaskMultiThreading : IDisposable
   {
       private readonly string _originalCwd = Directory.GetCurrentDirectory();
       private readonly string _decoyDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

       public GivenATheTaskMultiThreading()
       {
           Directory.CreateDirectory(_decoyDir);
           Directory.SetCurrentDirectory(_decoyDir);
       }

       public void Dispose()
       {
           Directory.SetCurrentDirectory(_originalCwd);
           try { Directory.Delete(_decoyDir, recursive: true); }
           catch (IOException) { /* tolerated */ }
           catch (UnauthorizedAccessException) { /* tolerated */ }
       }
   }
   ```
   This guarantees CWD restoration even if test setup throws, and avoids per-test boilerplate.

4. Do NOT place non-CWD-mutating tests in the same collection — it serializes them unnecessarily.

**Side-effect warning.** Introducing CWD-mutating tests can break OTHER existing tests in the same test project that load resources via bare filenames (e.g., `XDocument.Load("Strings.resx")`). Before committing, search the test project for bare-filename file loads and convert them to assembly-relative paths:

```csharp
Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Strings.resx")
```

(PR #53119 commit `1b20f35`, PR #53118 commit `c19ea45` — canonical examples.)

#### Decoy-CWD tests: input ItemSpecs MUST be relative; assert decoyDir does NOT contain the relative input

**CRITICAL — relative inputs only.** Every `ITaskItem` / `HintPath` / path-string property passed into the task in a decoy-CWD test MUST be a RELATIVE path (e.g., `new TaskItem("refs\\A.dll")`). If you pass an absolute path computed from `Path.Combine(projectDir, "A.dll")`, the decoy-CWD assertion becomes a no-op — absolute paths resolve identically regardless of CWD, and a regression to CWD-based resolution will pass undetected. Use a helper like `CreateProjectFile(projectDir, relativePath)` that creates the file under `projectDir` but returns only `relativePath` as the item spec. (PR #53942 commit `bedba02`.)

**CRITICAL — decoy-CWD precondition guard.** Before calling `Execute()`, assert:

```csharp
Directory.Exists(Path.Combine(_decoyDir, relativeInput)).Should().BeFalse(
    "the relative input must not exist under the decoy CWD, or CWD-fallback regressions can silently pass");
```

Or randomize the relative segment with `Path.GetRandomFileName()`. Without this guard, a genuine regression to CWD-based resolution can pass on machines whose process CWD happens to contain a matching subdirectory. (PR #53955 Copilot review discussion_r3099403496.)

#### Test helper hygiene

`TreatWarningsAsErrors` is on in this repo, so unused `using` directives (CS8019) break the build — strip them from new test files. DispatchProxy-based test helpers (e.g., `TestDriverProxy` inside `TaskEnvironmentHelper`) should be declared `internal` (or private nested), never `public`. (PR #52937.)

### Step 4: Apply Stub Migration

Add the attribute, interface, and `TaskEnvironment` property — but do NOT change any task logic. Use the canonical single-line property; full rationale, banned alternatives, and the pre-merge grep checklist live in [`multithreaded-task-migration.md` § Canonical TaskEnvironment property](./multithreaded-task-migration.md#canonical-taskenvironment-property).

```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
    /// <inheritdoc/>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    // ... NO other changes — code still uses original path resolution ...
}
```

> Verify `TaskEnvironment.Fallback` is exposed in the target branch before adopting (PR #53957 establishes the canonical name; earlier PRs used `TaskEnvironmentDefaults.Create()`).

#### Forbid `Directory.GetCurrentDirectory()` / lazy `ProcessTaskEnvironmentDriver` init in fallback getters

❌ DO NOT:
```csharp
get => _taskEnvironment ??= new TaskEnvironment(new ProcessTaskEnvironmentDriver(Directory.GetCurrentDirectory()));
```

✅ DO: rely on `TaskEnvironment.Fallback`. `Directory.GetCurrentDirectory()` and `Environment.CurrentDirectory` are themselves forbidden APIs — using them inside a fallback re-introduces the process-CWD dependency this migration eliminates, even if the task never calls `GetAbsolutePath`. (PR #53946 reverted exactly this; PR #53954 commit `c6cb5b9` "Remove TaskEnvironment fallback".)

#### Reject `TaskEnvironment?` nullable property and `?.GetAbsolutePath(x) ?? x` callsites

The full anti-pattern catalogue — including the `?. … .Value ?? Path.GetFullPath(x)` NRE trap on .NET Core, the AI-reviewer null-guard rejection, and the pre-merge grep checklist — lives in [`multithreaded-task-migration.md` § Canonical TaskEnvironment property](./multithreaded-task-migration.md#canonical-taskenvironment-property).

#### Polyfill visibility — types must be `public`, not `internal`

All polyfill types in `src/Tasks/Common/` that appear on a task's public surface MUST be declared `public`:

- `public class TaskEnvironment` (used as the type of the public `TaskEnvironment` property)
- `public readonly struct AbsolutePath` (return type of `GetAbsolutePath`, exposed via implicit string conversion)
- `public interface IMultiThreadableTask` (implemented by public task classes)
- `public sealed class MSBuildMultiThreadableTaskAttribute` (applied to public task classes)

Driver implementation classes (`ProcessTaskEnvironmentDriver`, `ITaskEnvironmentDriver`) may remain `internal`.

**Pre-flight check:**

```powershell
rg 'internal (sealed )?(class|struct|interface) (TaskEnvironment|AbsolutePath|IMultiThreadableTask|MSBuildMultiThreadableTaskAttribute)\b' src/Tasks/Common/
```

Must return zero hits. An internal polyfill produces `CS0053: Inconsistent accessibility: property type 'TaskEnvironment' is less accessible than property 'SomeTask.TaskEnvironment'` on the net472 build only — green on .NET Core, red in CI. (PR #52909 commit `d619766c`, PR #52936, PR #53946 review r3099019263.)

### Step 5: Verify Tests FAIL for Behavioral Reasons

```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj \
  --filter "FullyQualifiedName~TheTaskMultiThreading"
```

**ALL tests MUST compile and run, but FAIL.** A test that passes against a stub-migrated task is a no-op.

**Critical design rule:** every test must set CWD to a decoy directory (see Step 3 above). The stub migration adds a `TaskEnvironment` property but doesn't use it — the task still resolves paths against the process CWD.

### Step 6: Complete the Migration

Replace forbidden APIs **directly** — do NOT null-check `TaskEnvironment`. With the `= TaskEnvironment.Fallback` initializer, the property is never null at runtime.

- `Path.GetFullPath(x)` → `TaskEnvironment.GetAbsolutePath(x)`. If the original code relied on `Path.GetFullPath` for `..`-segment normalization, wrap at the call site: `Path.GetFullPath(TaskEnvironment.GetAbsolutePath(x).Value)`. Do NOT rely on `AbsolutePath.GetCanonicalForm()`. (Cross-ref [`multithreaded-task-migration.md` § Path Conversion Edge Cases](./multithreaded-task-migration.md#path-conversion-edge-cases).)
- `File.Exists(relativePath)` → `File.Exists(TaskEnvironment.GetAbsolutePath(relativePath))`
- `new FileStream(path, ...)` → absolutize `path` first
- `XDocument.Load(path)` / `.Save(path)` → absolutize `path` first; for concurrent readers prefer the `FileStream` + `XDocument.Load(stream)` recipe.

Store absolutized paths as `AbsolutePath`, not `string` — the type signals "this has been routed through `TaskEnvironment` and is safe for file I/O", and the implicit string conversion fires at the use site:

```csharp
// GOOD:
AbsolutePath assetsFilePath = TaskEnvironment.GetAbsolutePath(AssetsFilePath);
if (File.Exists(assetsFilePath)) { ... }       // implicit AbsolutePath -> string

// BAD: strips type-level proof of absolutization
string assetsFilePath = TaskEnvironment.GetAbsolutePath(AssetsFilePath);
```

When passing to APIs that take `string`, the implicit conversion handles it; for output metadata where you want to preserve the caller's input form, use `.OriginalValue` (see "AbsolutePath.OriginalValue for outputs" below).

#### Helper-method hotspot checklist; absolutize at task boundary

For the canonical "where to absolutize" rules (top of `ExecuteCore` vs point-of-use vs recursive vs derived-expression vs ToolTask override points), see [`multithreaded-task-migration.md` § Where to Absolutize](./multithreaded-task-migration.md#where-to-absolutize-point-of-use-patterns).

**Helper-method audit before completing the migration:** check (1) `ValidateParameters()`, (2) every Execute branch, (3) rollback/cleanup code, (4) private helper methods that combine paths. A path string that escapes into a helper without absolutization will silently fall back to CWD resolution.

**Helpers must NOT reference `TaskEnvironment` directly.** Pass pre-absolutized strings as parameters:

```csharp
private void AddFolder(StringBuilder sb, string absoluteLayoutDirectory, ...) { ... }
```

**Shared / non-task helper classes** (used by both migrated and un-migrated tasks): inject a path-resolver delegate into the constructor instead of coupling to `TaskEnvironment`:

```csharp
public ConflictItem(ITaskItem originalItem, ConflictItemType itemType,
                    Func<string, string>? pathResolver = null) { ... }

// ... in the migrated task:
private string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : (string)TaskEnvironment.GetAbsolutePath(path);

// un-migrated sibling tasks pass identity:
new ConflictItem(item, type, p => p);
```

This keeps the helper's API uniform across migrated/un-migrated callers and avoids nullable `TaskEnvironment?` fields. (PR #53942 `ResolvePackageFileConflicts.cs`, PR #53943 `ConflictItem.cs`.)

**Canonical examples:** PR #53950 `GenerateDepsFile` (task-level + point-of-use split), PR #52672 `ExtractArchiveToDirectory` (helper-hotspot audit), PR #53943 `ResolvePackageFileConflicts` (resolver delegate).

#### `AbsolutePath.OriginalValue` for output metadata

The canonical recipe for nullable `AbsolutePath?` + `.OriginalValue` for output metadata is in [`multithreaded-task-migration.md` § AbsolutePath Usage Pattern](./multithreaded-task-migration.md#absolutepath-usage-pattern). Two add-ons specific to interface-migration PRs:

**(a) Nullable disambiguation.** When the resolved value is itself `AbsolutePath?`, both layers carry meaning:

```csharp
if (resolved.HasValue && Directory.Exists(resolved.Value.Value))
{
    item.SetMetadata(MetadataKeys.PackageDirectory, resolved.Value.OriginalValue);
}
```

The outer `.Value` unwraps the nullable struct; the inner `.Value` is the absolute string for I/O; `.OriginalValue` is the caller's input form for output metadata.

**(b) Comment/code style rule.** If a comment references `AbsolutePath.OriginalValue`, the adjacent code MUST actually call `.OriginalValue`. Do not hand-roll a parallel `string originalX` and label it "OriginalValue" in comments — fix the code, not the comment. (PR #53955 discussion_r3099403471.)

#### Drive-relative paths and null/whitespace sentinels

`GetAbsolutePath` rejects Windows drive-relative rooted paths (`Path.IsPathRooted(@"\publish\")` is `true`, but `AbsolutePath` throws `ArgumentException` because the path is not fully qualified). Tasks with user-supplied `PublishDir`/`OutputDir`-style inputs must guard or re-root against `ProjectDirectory`'s drive before calling `GetAbsolutePath`. (PR #53949 review on `GenerateBundle.cs:64`.)

When forwarding to an external library that historically validates null/whitespace itself, preserve the sentinel:

```csharp
return string.IsNullOrWhiteSpace(sourcePath)
    ? sourcePath
    : TaskEnvironment.GetAbsolutePath(sourcePath).Value;
```

(PR #53949 `ResolveFileSpecSourcePath` helper, commit `93fb85ce` on `GenerateBundle.cs`.)

For the canonical handling of these and other path edge cases, see [`multithreaded-task-migration.md` § Path Conversion Edge Cases](./multithreaded-task-migration.md#path-conversion-edge-cases).

#### Net472 driver/process compatibility traps

When constructing `ProcessStartInfo` for the multiprocess driver, test harnesses, or any out-of-proc child process:

- Use `ProcessStartInfo.EnvironmentVariables` (`StringDictionary`, present on net472) — NOT `ProcessStartInfo.Environment` (`IDictionary<string,string>`, .NET 5+/netstandard2.1 only). (PR #52909 commit `d619766c`.)
- Set `UseShellExecute = false` UNCONDITIONALLY. On net472 the default is `true`, which silently drops the `EnvironmentVariables` dictionary and produces hard-to-diagnose multiprocess-vs-multithreaded parity failures. (PR #53120 commit `ff14127b`.)
- `Path.IsPathFullyQualified` does not exist on net472 — implement manually if you need it.

(`Microsoft.NET.Build.Tasks.csproj` still has `<TargetFrameworks>$(SdkTargetFramework);net472</TargetFrameworks>`, so these traps remain live.)

### Step 7: Verify Tests PASS

```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj \
  --filter "FullyQualifiedName~TheTaskMultiThreading"
```

**All new tests MUST pass now.**

### Step 8: Verify No Regressions

```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```

**All existing tests MUST still pass.**

#### Pattern B unit tests at legacy call sites must explicitly assign `TaskEnvironment`

Audit legacy in-proc test construction sites. Before opening the migration PR:

1. `rg 'new <TaskName>\(' test/`
2. For every hit, set `task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);` so the test exercises the real `IMultiThreadableTask` codepath (not the CWD-based `Fallback`).
3. Run the FULL test project, not just the new `GivenA<TaskName>MultiThreading.cs` file. The migration changes path-resolution behavior; pre-existing tests that relied on CWD-based resolution may fail with `FileNotFoundException` once the task starts using `TaskEnvironment.GetAbsolutePath`.
4. Include the updated legacy test files in the same migration PR.
5. Reject these workarounds in the task class: `public TaskEnvironment TaskEnvironment { get; set; } = new();`, `?? TaskEnvironmentHelper.CreateForTest()` getters, or any defensive runtime fallback that masks the contract.

For the canonical assignment patterns and the "single-assignment" factory rule, see [`multithreaded-task-migration.md` § Test setup conventions](./multithreaded-task-migration.md#test-setup-conventions-canonical-home).

#### Setter test rule

A test named `*PropertyExistsAndCanBeSet` MUST exercise the setter:

```csharp
task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
task.TaskEnvironment.Should().BeSameAs(taskEnvironment);
```

If you only intend to assert reflection metadata (`CanRead`/`CanWrite`), rename the test `…ExistsAndIsReadWrite`. Reflection-only assertions are not behavioral tests. (PR #53948 discussion_r3099377461; fix in commit `f02f582`.)

## Polyfills (Phase 0 — now upstream)

These types are provided upstream by the MSBuild Framework package (and gated `#if NETFRAMEWORK` polyfills in `src/Tasks/Common/`):

- `IMultiThreadableTask` — interface with `TaskEnvironment TaskEnvironment { get; set; }`
- `TaskEnvironment` — class with `GetAbsolutePath()`, `GetEnvironmentVariable()`, `ProjectDirectory`, `Fallback`, etc.
- `AbsolutePath` — struct with `Value`, `OriginalValue`, implicit string conversion
- `MSBuildMultiThreadableTaskAttribute` — attribute (already existed)

In test project:
- `TaskEnvironmentHelper.CreateForTest()` — creates `TaskEnvironment` with CWD as project dir
- `TaskEnvironmentHelper.CreateForTest(string projectDirectory)` — creates `TaskEnvironment` with specified project dir

Polyfill *authoring* guidance (visibility semantics beyond the migration-author rule above, normalization fidelity, attribute internal-vs-public) lives in the upstream foundation; this skill no longer covers it.

## Complete Migration Example: Path.GetFullPath Replacement

### Before (unsafe)

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

### After (correct migration)

```csharp
[MSBuildMultiThreadableTask]
public class PathNormalizer : Microsoft.Build.Utilities.Task, IMultiThreadableTask
{
    /// <inheritdoc/>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public string InputPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (string.IsNullOrEmpty(InputPath))
        {
            Log.LogError("InputPath is required.");
            return false;
        }

        AbsolutePath resolvedPath = TaskEnvironment.GetAbsolutePath(InputPath);
        Log.LogMessage(MessageImportance.Normal, $"Resolved path: {resolvedPath}");
        if (File.Exists(resolvedPath))
            Log.LogMessage(MessageImportance.Normal, $"File found at '{resolvedPath}'.");
        else
            Log.LogMessage(MessageImportance.Normal, $"File not found at '{resolvedPath}'.");
        return true;
    }
}
```

### Key differences

1. Added `IMultiThreadableTask` interface
2. Added `TaskEnvironment` property with `= TaskEnvironment.Fallback` initializer (single line, no `#if NETFRAMEWORK`)
3. Replaced `Path.GetFullPath(InputPath)` → `TaskEnvironment.GetAbsolutePath(InputPath)`
4. Local typed as `AbsolutePath` (not `string`) — implicit conversion at use sites

**Do NOT add `ProjectDirectory` self-initialization code.** MSBuild handles this automatically by providing a fully initialized `TaskEnvironment` to all `IMultiThreadableTask` implementations before `Execute()` is called. For unit tests, use `TaskEnvironmentHelper.CreateForTest(projectDir)` to set it manually.
