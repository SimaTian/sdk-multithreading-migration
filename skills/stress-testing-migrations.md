# Skill: Concurrency Testing for Multithreaded Task Migrations

## Purpose

After migrating a task to `IMultiThreadableTask`, run concurrency tests to verify the migration is correct under concurrent execution. Two tiers exist (see "Two tiers of concurrency tests" below): a deterministic regression that ships in CI forever, and an exploratory stress suite that is run locally and deleted before commit.

This skill defers to [`multithreaded-task-migration.md`](./multithreaded-task-migration.md) for the canonical `TaskEnvironment` property declaration, the test setup conventions (single-assignment factory rule), the `where to absolutize` checklist, and the shared-state/cache audit recipe. It owns concurrency-specific test patterns, the start-gate coordination recipe, and the xUnit collection-isolation rule.

## Two tiers of concurrency tests: what to commit, what to delete

Concurrency testing for a migrated task has two distinct tiers. Conflating them is the single most common review failure on migration PRs.

### Tier 1 — Deterministic concurrency regression test (COMMIT)

Exactly one focused test per migrated task that:

- Runs **2–4** instances concurrently (low enough to be fast and stable in CI)
- Uses **fixed inputs** and **fixed unique project directories**
- Asserts the **per-instance** resolved outputs (path / metadata / item set)
- Uses the start-gate pattern with bounded timeouts (see Pattern 1)
- Belongs to a CWD-safe xUnit `[Collection]` if the task touches CWD/env

This test stays in the repo forever. It is the regression guard.

### Tier 2 — Exploratory stress test (DO NOT COMMIT)

Runs with N=16/64/256, randomized inputs, optional soak loops. Used during migration to flush out races. After it passes locally:

1. Confirm Tier 1 still covers the same code path
2. **Delete the Tier 2 file before committing**
3. Note the local pass in the PR description

### Applicability gate — Pattern A tasks need no stress test of either tier

Skip both tiers of concurrency testing if **all** are true:

- The task does not call `TaskEnvironment.*` anywhere.
- The task has no instance fields that outlive `Execute()` (no caches, no lazy fields, no `RegisteredTaskObject`).
- The task only transforms `ITaskItem` metadata in-memory.

Document the applicability gate in the PR description; **never substitute a reflection-based "attribute is present" test** as a stand-in. Such tests are tautological and consistently rejected by reviewers (see `analyze-and-migrate-template.md` Test Design Rule 1).

## Key Findings

### TaskEnvironment is always provided

With the canonical `public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;` initializer (cross-ref [`multithreaded-task-migration.md` § Canonical TaskEnvironment property](./multithreaded-task-migration.md#canonical-taskenvironment-property)), the property is never null at runtime. Use it directly without null guards. **Tests must always set `TaskEnvironment`** to a `TaskEnvironmentHelper.CreateForTest(projectDir)` instance so they exercise the real codepath rather than the CWD-based passthrough.

### Absolute paths pass through GetAbsolutePath unchanged

`TaskEnvironment.GetAbsolutePath()` checks `Path.IsPathRooted()` — if the path is already absolute, it returns it as-is. The real value of `GetAbsolutePath` shows when the input is relative.

### AbsolutePath requires fully-qualified paths

The real MSBuild `AbsolutePath` struct validates that paths are fully qualified — not just rooted. On Windows, `\root\foo` is rooted but NOT fully qualified (missing drive letter). Test paths must use `Path.GetFullPath()` to ensure they have a drive letter before passing to `TaskEnvironmentHelper.CreateForTest()`.

### Instance state is per-task-instance (safe by design)

MSBuild creates a **new task instance per execution**. Fields are instance fields — they don't cross-contaminate between threads because each thread gets its own instance. No locking needed.

### Shared objects (LockFile, LockFileCache) must be read-only after creation

`LockFileCache` uses MSBuild's `RegisteredTaskObject` system to share parsed lock files across task instances. Sharing is safe only if the shared object is immutable after parsing. (See [`multithreaded-task-migration.md` § Shared State and Cache Audit](./multithreaded-task-migration.md#shared-state-and-cache-audit) for the full audit rules.)

## Stress Test Patterns

### Pattern 1: Concurrent execution with distinct project directories

The core test. N task instances run concurrently, each with its own `TaskEnvironment` pointing to a unique project directory. Use a two-gate start pattern (`CountdownEvent` ready-gate + `ManualResetEventSlim` start-gate) so all threads are *known* to be parked at the start line before being released — `Parallel.For` does NOT give you that guarantee.

```csharp
[Theory]
[InlineData(4)]
[InlineData(16)]
[InlineData(64)]
public async Task ConcurrentExecutionWithDistinctProjectDirs(int parallelism)
{
    using var ready = new CountdownEvent(parallelism);
    using var start = new ManualResetEventSlim(false);
    using var cts   = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var errors      = new ConcurrentBag<string>();

    var tasks = Enumerable.Range(0, parallelism).Select(i => Task.Run(() =>
    {
        var projectDir = CreateUniqueProjectDir(i);
        var task = CreateTaskForProjectDir(projectDir, i);
        task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);

        ready.Signal();
        start.Wait(cts.Token);            // every wait has a timeout / CT

        if (!task.Execute())
            errors.Add($"task {i} failed");
        // Verify resolved paths match expected for THIS project dir
    }, cts.Token)).ToArray();

    ready.Wait(cts.Token);                 // all workers parked at the gate
    start.Set();                           // release them simultaneously
    await Task.WhenAll(tasks);             // xUnit1031: never .Wait()/.Result

    errors.Should().BeEmpty();
}
```

**What it catches**: Cross-talk between threads via shared mutable state, static fields, or CWD dependency.

### Forbidden coordination patterns

- ❌ `Parallel.For(0, N, ..., i => { barrier.SignalAndWait(); ... })` — `Parallel.For` may run iterations sequentially on a single worker, so `Barrier(N)` deadlocks. Use `Task.Run` per worker instead.
- ❌ Any untimed `barrier.SignalAndWait()`, `mre.Wait()`, `event.Wait()`, `task.Wait()`, `task.Result`. All waits must take either a `TimeSpan` timeout or a `CancellationToken` from a `CancellationTokenSource` with a bounded `TimeSpan`. Otherwise a regression hangs the test runner instead of failing.
- ❌ Synchronous `void` test bodies that call `.Wait()` / `.Result` on `Task.WhenAll`. Use `async Task` + `await` (xUnit1031).
- ❌ Synchronization primitives without `using var` — `CountdownEvent`, `ManualResetEventSlim`, `Barrier`, `CancellationTokenSource` are all `IDisposable`.
- ❌ Capturing the `Parallel.For` / `for` loop variable inside a closure without copying it to a local first.

### Pattern 2: Same data, different project directories

Multiple tasks process identical lock file content but from different project directories. The same relative path (e.g., `../ClassLib/ClassLib.csproj`) must resolve to different absolute paths.

**Key test design detail**: Each project dir must have a **unique parent directory**. If all project dirs are siblings under `Temp/`, then `../ClassLib` resolves to the same path for all of them. Use nested dirs:

```
Temp/rpd-stress-<guid1>/proj0/myproject.csproj  →  ../ClassLib = Temp/rpd-stress-<guid1>/ClassLib
Temp/rpd-stress-<guid2>/proj1/myproject.csproj  →  ../ClassLib = Temp/rpd-stress-<guid2>/ClassLib
```

### Pattern 3: All tasks always have TaskEnvironment set

MSBuild always provides `TaskEnvironment` to `IMultiThreadableTask` implementations. Verify N concurrent tasks all work correctly with `TaskEnvironment` always set, each pointing to a distinct project directory.

### Pattern 4: Shared LockFile object

Multiple tasks share a single `LockFile` instance (simulating `LockFileCache` behavior in real builds). Verifies no shared mutable state leaks through the lock file.

### Pattern 5: Relative ProjectPath

Set `ProjectPath` to a relative path like `subdir/myproject.csproj`. This exercises `GetAbsolutePath` with a relative input from `Path.GetDirectoryName()`, proving the task resolves it via `TaskEnvironment.ProjectDirectory` rather than CWD.

### Pattern 6: Edge cases

- Paths with spaces and special characters (`My Lib`, `(1)`)
- Multiple project references in the same lock file with different relative paths
- Repeated execution with fresh instances to verify no state accumulation
- **CWD stability check**: verify `Directory.GetCurrentDirectory()` is unchanged after task execution
- **Cross-platform path separators**: build expected paths with `Path.Combine` / `Path.DirectorySeparatorChar`; never hard-code `\` or `/` in assertions. (PR #53119, PR #52937.)

### Pattern 7: Once-per-build guard race-test recipe

For tasks guarded by `Interlocked` / `RegisteredTaskObject` once-per-build gates: race the gate with N concurrent `Execute()` calls; assert the guarded side-effect ran exactly once (e.g., a counter incremented to 1, a log line emitted once). The test should fail if the gate is removed. (PR #53956.)

## Per-thread output assertion (mandatory)

A concurrency test that only catches exceptions is not a concurrency test. Each worker must capture its resolved outputs into a `ConcurrentDictionary<int, ExpectedShape>` keyed by worker index, and the test body must assert *each* entry equals the value computed from *that* worker's inputs. To make cross-talk visible, give each worker a **distinct payload** (different project dir, different lock-file content, different metadata value) — identical payloads cannot detect a swap. (PR #53117, PR #53942, PR #52555.)

## Test isolation for process-wide state

Anything that mutates per-process state — CWD, environment variables, `RegisteredTaskObject` singletons keyed off the process — MUST be serialized. xUnit parallelizes across collections by default, and parallel CWD mutations corrupt sibling tests (on macOS this surfaces as a "directory was deleted" crash; on Windows as bare-filename file load failures).

Define one shared collection per process-wide concern:

```csharp
[CollectionDefinition("CWD-Dependent", DisableParallelization = true)]
public class CwdDependentCollection { }

[CollectionDefinition("ProcessEnv-Dependent", DisableParallelization = true)]
public class ProcessEnvCollection { }
```

Every test class that mutates CWD or env vars (directly, or via `TaskTestEnvironment` / setup helpers that do) must be tagged:

```csharp
[Collection("CWD-Dependent")]
public class MyMigratedTaskTests { ... }
```

Env-var isolation tests must additionally **set conflicting values in both the process env and the `TaskEnvironment`**, then assert the task read from `TaskEnvironment` — otherwise the test would pass even if the task incorrectly read from the process env.

## Test Infrastructure

### Temp directory management

Use `ConcurrentBag<string>` (not `List<string>`) for tracking temp directories in concurrent tests. Replace bare `catch` with scoped exception handling that logs:

```csharp
private readonly ConcurrentBag<string> _tempDirs = new();

public void Dispose()
{
    foreach (var dir in _tempDirs)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException ex)                 { _output.WriteLine($"temp cleanup: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { _output.WriteLine($"temp cleanup: {ex.Message}"); }
    }
}
```

Bare `catch { }` swallows the regression you actually want to see. (PR #53943.)

### Creating test tasks with project references

To trigger `GetAbsolutePathFromProjectRelativePath`, the lock file must contain a library with `type: "project"` and a non-empty `msbuildProject` field:

```csharp
string classLibDefn = CreateProjectLibrary("ClassLib/1.0.0",
    path: "../ClassLib/project.json",
    msbuildProject: "../ClassLib/ClassLib.csproj");  // ← this triggers the forbidden API
string targetLib = CreateTargetLibrary("ClassLib/1.0.0", "project");
```

Reference metadata keys via the `MetadataKeys` constants (`MetadataKeys.PackageName`, `MetadataKeys.ParentTarget`, etc.) — never raw strings. A typo in a string literal silently passes the test. (PR #53116.)

Required path properties (`ProjectPath`, `ProjectAssetsFile`, etc.) must be set to non-empty values in fixtures even when not directly under test — many tasks short-circuit on null/empty inputs and the concurrency code path will never run. (PR #53121.)

### Setting TaskEnvironment on migrated tasks

**Per-task tests that target one already-migrated task** — drop reflection, assign directly:

```csharp
task.TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir);
```

**Cross-cutting helpers that run against mixed migrated/un-migrated tasks** — reflection remains transitional only as long as un-migrated targets exist; delete the reflection block once all targets in scope are migrated:

```csharp
// transitional only — remove once every target task implements IMultiThreadableTask
var teProp = task.GetType().GetProperty("TaskEnvironment");
teProp?.SetValue(task, TaskEnvironmentHelper.CreateForTest(projectDir));
```

**Single-assignment factory rule (always applies).** If a shared test factory constructs and pre-configures the task, assign `TaskEnvironment` ONCE — either in the factory OR at the call site, never both. Double-assignment masks bugs where per-test customization is silently overwritten. Cross-ref [`multithreaded-task-migration.md` § Test setup conventions](./multithreaded-task-migration.md#test-setup-conventions-canonical-home).

## Checklist for Concurrency-Testing a Migrated Task

- [ ] Confirm the task is NOT in the Pattern A applicability gate above
- [ ] Identify which method(s) use `TaskEnvironment` (the migrated forbidden API calls)
- [ ] Identify what input properties trigger those code paths
- [ ] Write Tier 1 deterministic test (2–4 instances, fixed inputs, per-instance assertions, start-gate with timeouts, `[Collection]` if CWD/env mutating)
- [ ] Write Tier 2 stress tests locally (high N, randomized inputs); confirm they pass; **delete before commit**
- [ ] Verify CWD is not modified during execution
- [ ] Use `Path.Combine`/`Path.DirectorySeparatorChar` in expected values; never hard-code separators
- [ ] All required path properties non-empty in fixtures
- [ ] All test files use `MetadataKeys.*` constants, not string literals
