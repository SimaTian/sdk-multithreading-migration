# Skill: Analyze-and-Migrate Task Template (TDD Approach)

## When to Use

Use this template when migrating a task that MAY or MAY NOT use forbidden APIs. The agent must read the full task source, determine the correct migration approach, and follow a strict TDD workflow.

This skill defers to `multithreaded-task-migration.md` for canonical rules on Pattern A semantics, the `TaskEnvironment` property declaration, the `where to absolutize` checklist, and test setup conventions. It owns the **decision gate**, **PR hygiene**, **test design rules**, and **legacy-test audit** workflow.

## Core Principle: Tests First, Migration Second

Every migration follows this strict order:

1. **Step 0** — Decision gate (is the task already enlightened?)
2. Analyze the task → 3. Write behavioral tests → 4. Apply stub migration → 5. Verify tests FAIL for behavioral reasons → 6. Complete the migration → 6b. Audit ALL pre-existing tests → 7. Verify tests PASS → 8. Verify no regressions

## Step 0 — Pre-Analysis: Is the task already enlightened?

Before any analysis or coding, run these checks against the task source on `main`:

1. `rg '\[MSBuildMultiThreadableTask\]' <task-file>` — is the attribute already present?
2. `rg ': .*IMultiThreadableTask' <task-file>` — is the interface already declared?
3. `rg 'TaskEnvironment\s+TaskEnvironment' <task-file>` — is the property already declared?

Decision matrix (combined with Step 2's forbidden-API scan):

| Attribute present? | Forbidden APIs found? | Action |
|---|---|---|
| Yes | No  | **STOP.** Task is already Pattern A complete. Do NOT add `IMultiThreadableTask` or `TaskEnvironment`. Close the tracking item, run Step 8 regression check, move on. |
| Yes | Yes | Continue with Pattern B from Step 3 onward. |
| No  | No  | Pattern A migration: add attribute only (see decision gate below). |
| No  | Yes | Full Pattern B migration. |

**Canonical negative examples:**
- PR #53952 — 570-line Pattern A "migration" of `FindItemsFromPackages` reverted because the attribute was already on `main` (*"The task is already enlightened, the tests looks to me having very limited value"* — @AR-May).
- PR #53954 — `IMultiThreadableTask` and full test file added to a task that already had the attribute; reviewer asked for full revert (*"the attribute is already there"* — @JanProvaznik).
- PR #53956 — `c5ebee75` removes `: IMultiThreadableTask` and 154-line test file from `ShowPreviewMessage`.

### Step 0c — Classify the task

Write a one-line classification before writing any migration code:

- **`pure-metadata`** — no FS, no env, no process spawn; pure data transformation. Apply only `[MSBuildMultiThreadableTask]`. Do NOT create a `GivenA<Task>MultiThreading.cs` file. Existing behavioral coverage is sufficient. (PR #53946 `ValidateExecutableReferences` — canonical scope-creep example; final commit `638773b` deleted the test file as "unnecessary tests".)
- **`FS-reading`** — Pattern B; full migration.
- **`process-spawning`** — ToolTask checklist applies (cross-ref `multithreaded-task-migration.md` "Where to absolutize").
- **`env-var-reading`** — Pattern B; ensure all reads route through `TaskEnvironment.GetEnvironmentVariable`.

## PR Readiness Gate (Pattern B)

A Pattern B migration PR that contains zero test file modifications will not pass review. Hard rules:

- For every batch PR migrating N Pattern B tasks there MUST be N corresponding `GivenA<TaskName>MultiThreading.cs` test files (see "One test file per task").
- Reviewers should request tests immediately and not begin code review until they exist.
- Pattern A (attribute-only) PRs are exempt from per-task test files (see Step 0 decision gate); they may still require an existing-test audit if new locking is introduced.

> Canonical rejection: PR #52672 — 20 migrations across two assemblies, zero test files, stacked on unmerged dependency PR #52554, closed without review.

## PR Scope and Batching

1. **One assembly per PR.** Do NOT mix `Microsoft.NET.Build.Tasks` and `sdk-tasks` in one PR. (PR #52672.)
2. **Do NOT stack on an unmerged dependency.** Either wait for the parent PR to merge and rebase, OR open a draft PR targeting the parent's branch as base. Do NOT instruct reviewers to "ignore the first commit". (PR #52672 PR body: *"only second commit is for review. the other one will go away when [#52554] is merged"* — closed without review.)
3. **Cap PR size** at ~10 files / ~150 LOC unless changes are pure mechanical repeats. State verification commands run (build + targeted tests) in the PR description.

## Test Design Rules

1. **Do NOT generate tests for attribute or interface implementation** — `[MSBuildMultiThreadableTask]`, `BeAssignableTo<IMultiThreadableTask>()`, etc. **Strong ban applies to reflection-based variants too.** No narrow exceptions.

   > **NEVER commit** reflection-based attribute or interface presence tests (e.g., `taskType.GetCustomAttributes().Select(a => a.GetType().FullName).Should().Contain("...MSBuildMultiThreadableTaskAttribute")`, `BeAssignableTo<IMultiThreadableTask>()`, `HasCustomAttribute<...>()`). Reflection does not change the calculus — these tests verify decoration, not behavior, and pass trivially once the attribute is added.
   >
   > **Pre-push check:** grep the test file for `MSBuildMultiThreadableTaskAttribute` and `IMultiThreadableTask` in assertion strings; delete any test whose sole purpose is to assert these are present. (PRs #53945, #53949, #53951.)

2. **All tests inject `TaskEnvironment`. Vary input shape, not the presence of `TaskEnvironment`.** Once a task is migrated, do NOT write tests that run with vs. without `TaskEnvironment` set:
   - Tests that "run without TaskEnvironment as a baseline" are invalid — `TaskEnvironment.Fallback` makes them indistinguishable from the injected case.
   - Tests asserting that omitting `TaskEnvironment` throws are wrong by construction — the canonical default never throws.

   Instead, use **paired executions that differ only in input shape**:
   - absolute path vs relative path for the same logical input;
   - decoy CWD vs project-dir CWD;
   - presence vs absence of an env var via `TaskEnvironment.GetEnvironmentVariable`.

   Each test must include both a relative-path input that exercises `TaskEnvironment` AND an absolute-path input that proves passthrough. Assert the two runs produce identical observable outputs.

3. **One test file per task.** Even when migrating multiple tasks in the same PR, create a separate `GivenA<TaskName>MultiThreading.cs` for each. Shared group files (e.g., `GivenAttributeOnlyTasksGroup5.cs`) are consistently rejected (PR #53116).

4. **Local stress testing (not committed).** Run concurrent execution tests locally during migration to verify thread safety, but do NOT include high-N stress tests in the final committed test suite. See `stress-testing-migrations.md` for the two-tier policy.

5. **Preserve all public properties.** Every public property on the original task must exist on the migrated task. Dropping, renaming, or changing the type of any public property is a migration error.

6. **Output properties must preserve input form (relative stays relative).** The migration absolutizes paths *internally* for correct file I/O, but Output properties exposed to MSBuild consumers must preserve the original form of the input. Test by providing relative input paths and asserting the Output property values are still relative after execution. Cross-ref `multithreaded-task-migration.md` "AbsolutePath Usage Pattern" for the canonical recipe.

7. **Use `MetadataKeys.*` constants** (`src/Tasks/Common/MetadataKeys.cs`) for ALL well-known metadata key references — both in production code being touched by the migration and in new test code. Example: `{ MetadataKeys.NuGetPackageId, "MyPackage" }` — NOT `{ "NuGetPackageId", "MyPackage" }`. Reviewers consistently request this (PRs #53116, #53951).

### Anti-pattern — single-item output-relativity test

Providing only one `ITaskItem` to a conflict-resolving task is a no-op: without a conflict, the resolver never reads file metadata, CWD-based resolution is never triggered, and the test passes against a stub migration. Output-relativity tests MUST:
- include ≥2 items with the same logical filename but different source paths (e.g. `lowItem` lower version, `highItem` higher version),
- set CWD to a decoy directory,
- assert the resolver picks the expected winner AND preserves the original relative form of `ItemSpec` in the output.

(PR #53943 commit `c0df00a` — corrective pattern.)

### Anti-pattern — guarded assertion

Never wrap the main assertion behind an `if`-condition (e.g. `if (!string.IsNullOrEmpty(value)) value.Should()...`). When the condition is false the test passes vacuously. Either:
- (a) unconditionally assert the value/token EXISTS first, then assert its content; or
- (b) replace internal data-snooping with a paired behavioral comparison (execute with absolute vs relative path, assert identical observable outputs) — impossible to pass accidentally.

(PR #53950 commit `5dc9a2b` — `ProjectPathIsAbsolutized` → `InjectedTaskEnvironmentProducesSameDepsJsonForAbsoluteAndRelativeProjectPathInputs`.)

### xUnit `[Collection("CWD-Dependent")]` is REQUIRED for any CWD-mutating test class

> **REQUIRED — xUnit serialization.** Any test class whose tests call `Directory.SetCurrentDirectory()` MUST be annotated with `[Collection("CWD-Dependent")]` on the class declaration. Pair with the `[CollectionDefinition("CWD-Dependent", DisableParallelization = true)]` rule in `interface-migration-template.md`. xUnit runs test classes in parallel by default; two classes mutating CWD simultaneously produce intermittent `FileNotFoundException` and `DirectoryNotFoundException` failures. (PR #53120 commit `f16af3e` retrofitted on 4 classes after intermittent CI failures.)

## Process

### Step 1: Clone & Read

```bash
git clone https://github.com/SimaTian/sdk.git && cd sdk && git checkout main
```

Read the task source file completely.

### Step 2: Analyze for Forbidden APIs

#### Step 2a — Pattern A rapid disqualifier checklist (run first)

Scan each task for these instant disqualifiers BEFORE deeper analysis. Any hit ⇒ task is NOT Pattern A.

1. Any method whose name contains `Read`, `Write`, `Open`, `Create`, `Exists`, `Delete`, `Load`, `Save`, `Copy`, `Move` — even on helpers or library types.
2. Any field/property named `*Cache*`, `*_cache*`, `*_dict*`, `*_map*` that is a mutable collection — verify it is not shared via static state or `RegisteredTaskObject`.
3. Any call to `IBuildEngine` callbacks: `BuildEngine.LogTelemetry`, `BuildEngine.Log*Event`, `BuildEngine4.RegisterTaskObject` / `GetRegisteredTaskObject` (when used as a mutable shared cache), and any `IBuildEngine6/7/8` callback. **`IBuildEngine` is not thread-safe.** Tasks reaching these — directly or via `TaskBase` telemetry helpers — are NOT Pattern A and must NOT receive `[MSBuildMultiThreadableTask]`. (PR #52554, `AllowEmptyTelemetry`.)
4. Any call to `Environment.*`, `Path.GetFullPath`, `ProcessStartInfo`, `ZipFile.*`, symbolic/hard link creation, registry access, environment-variable mutation, or P/Invoke touching the filesystem.
5. `BuildEngine4.GetRegisteredTaskObject` / `RegisterTaskObject` — even if otherwise Pattern A, this is a once-per-build side effect that becomes racy under multithreading. The migration plan must include a `static readonly object` lock + double-checked register block (cross-ref `multithreaded-task-migration.md` "Shared State and Cache Audit"). (PR #53956.)

Grepping for `File.*`/`Path.*` alone has ~30% false-positive rate — read the FULL Execute path including TaskBase plumbing.

#### Step 2 — Forbidden API scan

Search the task code for ALL of these patterns:

- `Path.GetFullPath(` — needs `TaskEnvironment.GetAbsolutePath()`
- `File.Exists(`, `File.Open(`, `File.Create(`, `File.ReadAllText(`, `File.WriteAllText(`, `File.Delete(`, `File.Copy(`, `File.Move(` — paths must be absolute
- `Directory.Exists(`, `Directory.CreateDirectory(`, `Directory.Delete(` — paths must be absolute
- `new FileStream(`, `new StreamReader(`, `new StreamWriter(` — paths must be absolute
- `XDocument.Load(string path)` / `XDocument.Save(string path)` — resolves against CWD AND holds an exclusive lock. Fix: absolutize via `TaskEnvironment.GetAbsolutePath()`, then open an explicit `new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)` in a `using`, and pass the stream to `XDocument.Load(stream)`. Do NOT simply substitute the absolutized string into `XDocument.Load(absolutePath)` — concurrent readers will collide on the exclusive lock. (PR #52672 — `GetDependencyInfo.cs`, `OverrideAndCreateBundledNETCoreAppPackageVersion.cs`.)
- `FileVersionInfo.GetVersionInfo(` — path must be absolute
- `Environment.GetEnvironmentVariable(`, `Environment.SetEnvironmentVariable(` — needs `TaskEnvironment`
- `Environment.CurrentDirectory` — needs `TaskEnvironment.ProjectDirectory`
- `new ProcessStartInfo(`, `Process.Start(` — needs `TaskEnvironment.GetProcessStartInfo()`
- `Environment.Exit(`, `Environment.FailFast(` — forbidden, must remove
- `Console.` — forbidden

Also trace path strings through helper method calls — a path might flow into a helper that internally uses File APIs.

#### Step 2d — Transitive call-graph audit (mandatory)

Forbidden APIs hide one or two hops away from the task body. For every method called from `Execute()`/`ExecuteCore()`:

1. **Walk the call graph 1–2 hops deep.** Read the full body of every helper (private methods, internal `*Utilities`/`*Helpers` classes in the same repo, value-object constructors like `ResolvedFile`, cache classes like `LockFileCache`/`RuntimeGraphCache`, preprocessor classes like `NugetContentAssetPreprocessor`).
2. **`grep` each touched file** for: `Environment.GetEnvironmentVariable`, `Path.GetFullPath`, `Path.IsPathRooted`, `Environment.CurrentDirectory`, `Directory.GetCurrentDirectory`, `File.*`, `Directory.*`, `ZipFile.*`, `XDocument.Load`, `Console.*`.
3. **Classify each hit** (cross-ref `multithreaded-task-migration.md` "Transitive forbidden APIs"): `migrate` / `absolutize-at-caller` / `postpone-or-block`. **A TODO is NOT sufficient for a known transitive violation on the main path.**
4. **`ITaskItem` collections.** Grep for `.ItemSpec` and `.GetMetadata(...)` where the value flows into a constructor/method that performs I/O (e.g. `new FileSpec(sourcePath: item.ItemSpec, ...)`). Each such site needs `TaskEnvironment.GetAbsolutePath(item.ItemSpec)`. (PR #53949 missed this on the initial commit.)
5. **`static readonly` env-var fields.** Grep for `static readonly .* = Environment.GetEnvironmentVariable`. Type-load-time captures bypass `TaskEnvironment` AND share state across all task instances. Delete the field; read inline via `TaskEnvironment.GetEnvironmentVariable` in `ExecuteCore`. (PR #53944, `s_allowCacheLookup`.)
6. **External library tracing/logging.** Inspect external library helpers for `Console.Write*`, `Console.Error.Write*`, or other process-global sinks. These cannot be rerouted via `TaskEnvironment`. Treat as a migration blocker; require an upstream callback API change. (PR #53949 `HostModel.Bundle.Trace`.)

##### Known-sink registry (extend as you find more)

Sinks that internally call forbidden APIs even when the task body looks clean:

| Sink | Hidden call | First seen |
|---|---|---|
| `LockFileCache.GetLockFile(path)` | `Path.IsPathRooted` enforcement | PR #53117, #53120 |
| `RuntimeGraphCache.GetRuntimeGraph(path)` | `Path.IsPathRooted` enforcement | PR #53120 |
| `DependencyContextBuilder(..., projectPath, ...)` | `Path.GetFullPath(Path.Combine(...))` | PR #52936 |
| `ResolvedFile` constructor | `Path.GetFullPath()` | PR #52936 |
| `NugetContentAssetPreprocessor` | `File.Exists`, `File.OpenRead`, `File.WriteAllText`, `Directory.CreateDirectory` | PR #53117 |
| `DotNetReferenceAssembliesPathResolver.Resolve()` | `Environment.GetEnvironmentVariable("DOTNET_REFERENCE_ASSEMBLIES_PATH")` + `Directory.Exists` | PR #52936 |
| `PublishMutationUtilities.ChangeEntryPointLibraryName` | filesystem access | PR #52555 |
| `HostModel.Bundle.Trace` | `Console.WriteLine` / `Console.Error.WriteLine` | PR #53949 |
| `XDocument.Load(path)` | implicit CWD-based path resolution + exclusive lock | PR #52672 |
| `StoreArtifactParser.Parse(path)` | opens the manifest from disk despite no `File.*` in task body | PR #52554 |

**AI agents systematically classify external-library `Environment.*` and filesystem calls as "safe". Humans must verify these specifically.**

> Initial automated/LLM-assisted analysis of attribute-only candidates has a documented ~30–68% false-positive rate (PR #52554: 13/35 reverted; PR #52555: 13/19 reverted). Do not rely on automated agents alone for the safe-list.

### Decision gate at end of Step 2

After Step 2's scan completes, classify the task:

- **Pattern A (attribute-only)** — Zero forbidden APIs found. Apply ONLY the `[MSBuildMultiThreadableTask]` attribute. Do NOT add `IMultiThreadableTask`, do NOT add a `TaskEnvironment` property, do NOT create a new `GivenA<TaskName>MultiThreading.cs` test file. Cross-ref [`multithreaded-task-migration.md` § Pattern A](./multithreaded-task-migration.md#pattern-a-attribute-only-pure-in-memory-tasks).
- **Pattern B (interface migration)** — One or more forbidden APIs found, OR the task uses `BuildEngine4.GetRegisteredTaskObject`/`RegisterTaskObject` for cross-task shared state. Proceed with Steps 3–8 in full.

> Adding `IMultiThreadableTask` to a task with no forbidden APIs is a reviewer-requested rollback. See PR #53956 (`ShowPreviewMessage` — 154-line test file deleted) and PR #53954 as canonical examples.

### Step 3: Write Failing Tests FIRST (before any task code changes)

Create test file in `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/`.

**IMPORTANT: Do NOT write tests for attribute or interface presence** — including reflection-based variants. See Test Design Rule 1 above.

**For attribute-only tasks** (no forbidden APIs found): no per-task test file is needed. Existing behavioral coverage is sufficient.

**For interface-based tasks** (forbidden APIs found): write the focused decoy-CWD / output-relativity behavioral regression. Use representative non-empty inputs that force all helper calls and side effects:

> Empty-input smoke tests are insufficient evidence for Pattern A — they will not reveal hidden file/folder access or shared-state mutations inside helper methods. (PR #52555: 13/19 attributes removed after representative-input testing exposed hidden helper I/O.)

#### Decoy-CWD test design must be enforceable from PR description

> The tests you commit must implement the exact behavioral scenarios claimed in the PR description. Common mismatch: authors describe decoy-CWD and multi-process parity tests in the PR body, but write tests that use the SAME directory for both `TaskEnvironment.ProjectDirectory` and the process CWD — so the stub migration passes by accident.
>
> Before submitting, verify each claimed scenario:
> - (a) uses a decoy CWD distinct from `TaskEnvironment.ProjectDirectory`,
> - (b) creates files only under the project directory (not CWD),
> - (c) asserts outputs preserve the original relative `ItemSpec` values.
>
> Every synthetic `projectDir`/`decoyDir` passed to `TaskEnvironmentHelper.CreateForTest` must be fully qualified first: `Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"pkgdir-mt-{Guid.NewGuid():N}"))`. `AbsolutePath`-based helpers must never receive drive-less paths.

(PR #53944 reviewer caught the mismatch; PR #53120 diff is the canonical full-qualification recipe.)

### Step 4: Apply Stub Migration

Add the attribute, interface, and `TaskEnvironment` property — but do NOT change any logic. The task code still uses its original path resolution. This makes the tests compile and run.

**For attribute-only tasks:**
```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase
{
    // ... unchanged code ...
}
```

**For interface-based tasks** — use the canonical single-line property (cross-ref [`multithreaded-task-migration.md` § Canonical TaskEnvironment property](./multithreaded-task-migration.md#canonical-taskenvironment-property)):

```csharp
[MSBuildMultiThreadableTask]
public class TheTask : TaskBase, IMultiThreadableTask
{
    /// <inheritdoc/>
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    // ... NO other changes — code still uses original path resolution ...
}
```

> **Pitfall — never use `new TaskEnvironment(new ProcessTaskEnvironmentDriver(Directory.GetCurrentDirectory()))` as a fallback.** That captures the process CWD at property-access time and completely defeats thread-safety. The dual-targeted `#if NETFRAMEWORK` lazy-init pattern is now obsolete — `TaskEnvironment.Fallback` provides correct behavior on all target frameworks. Under `#nullable disable` files, omit the initializer entirely (no `?`, no `= null!`) to avoid CS8632. (PR #53949 commit `334f8ded`, PR #53957.)

> Verify `TaskEnvironment.Fallback` exists in the target branch. If not, fall back to a simple auto-property and assign in tests.

### Step 5: Verify Tests FAIL for Behavioral Reasons

```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj \
  --filter "FullyQualifiedName~TheTaskMultiThreading"
```

**ALL tests MUST compile and run, but FAIL.** A test that passes against a stub-migrated task is a no-op — it does not validate the migration and must be redesigned.

**Critical design rule: every test must set CWD to a decoy directory.** The stub migration adds a `TaskEnvironment` property but doesn't use it — the task still resolves paths against the process CWD. Tests that leave CWD at its default (or set it to the projectDir) will pass even with the stub, because CWD-based resolution happens to find the files. To catch this:

- Create files **only** under `projectDir`
- Set CWD to a **different, empty** `decoyDir` before running the task
- The task receives `TaskEnvironmentHelper.CreateForTest(projectDir)` — if it uses `TaskEnvironment`, it finds files; if it uses CWD, it gets `FileNotFoundException`/`DirectoryNotFoundException`

**How each test type should fail against the stub:**

1. **Output-relativity test** — Sets CWD to decoyDir. With the stub, task can't find files via CWD → throws `FileNotFoundException` → test asserts this is NOT a file-not-found error.
2. **Paired absolute-vs-relative test** — Runs with absolute then relative input under a decoy CWD; asserts identical outputs. With the stub, the relative case fails to find files.

### Step 6: Complete the Migration

Now apply the actual path absolutization changes. Follow `interface-migration-template.md` for detailed replacement steps and `multithreaded-task-migration.md` "Where to absolutize" for the canonical hotspot rules.

#### Verify every absolutization has a downstream file-I/O consumer

After listing every path-typed value to absolutize, trace each one to a concrete `File.*`/`Directory.*`/`new FileStream`/`XDocument.Load(stream)` call site. For each collection/dictionary whose values you absolutize in a loop, verify the collection is actually passed to a method that performs I/O — not just used for a boolean validation check (e.g., `TryCreate...` returning success/failure). If the downstream consumer doesn't exist, do NOT add the `GetAbsolutePath` call — it is dead code that confuses reviewers and inflates the diff. (PR #53119 — `typeLibIdMap`.)

#### Document deliberate omissions

Absolutize ONLY paths that flow into APIs that implicitly resolve against CWD (`File.*`, `Path.GetFullPath`, `new FileStream`, `Directory.*`, `XDocument.Load(stream-via-FileStream)`). Inputs passed verbatim into helpers that never touch the filesystem (e.g., `HostWriter` signatures, log-message formatters, manifest emitters taking a literal string) MUST be left alone. Over-absolutizing creates needless diff and can break callers expecting the original spelling. Document deliberate omissions with a one-line comment to forestall reviewer questions:

```csharp
// not absolutized: HostWriter never uses this as a filesystem path
```

(PR #52938 — `CreateAppHost.cs`.)

#### Keep the production diff minimal

Do NOT introduce new internal static helpers, new method overloads taking `TaskEnvironment` as an explicit parameter, or change method visibility in production code purely to enable path-resolution unit testing. If a helper is genuinely needed, make it a `private` instance method that reads `this.TaskEnvironment` directly. Reviewers will request removal of explicit-parameter helpers and the tests that depend on them. (PR #53949 @OvesN reviews on `GenerateBundle.cs`.)

#### `AbsolutePath` normalization is the caller's responsibility

Pitfall — do NOT add `Path.GetFullPath` normalization inside shared helpers like `AbsolutePath`'s combining constructor. The repo's `AbsolutePath` mirrors the real MSBuild polyfill where canonicalization is the caller's responsibility. Wrapping `Path.GetFullPath` centrally changes semantics for every caller, introduces hidden filesystem I/O inside what looks like a pure string operation, and is incompatible with the multi-threaded-task purity analysis. Normalize at the call site if needed. (PR #53116 commit `385b4f1`.)

### Step 6b — Audit ALL pre-existing tests for the migrated task

After completing the Pattern B migration:

1. `rg 'new <TaskName>\(' src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/` — find every existing instantiation.
2. For each hit, ensure the test:
   - sets `TaskEnvironment = TaskEnvironment.Fallback` (or `CreateForTest(...)`),
   - sets `BuildEngine`,
   - uses absolute paths for any input that the migrated task now passes through `GetAbsolutePath()` (because `GetAbsolutePath` returns absolute paths and existing assertions may expect a particular form).
3. Run `dotnet test --filter "FullyQualifiedName~<TaskName>"` and verify no `NullReferenceException` regressions.
4. **Include the updated legacy test files in the same migration PR.**

(PR #53117 required 8 retrofits in `GivenAProduceContentsAssetsTask.cs` that were missed in the initial migration commits.)

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

## ToolTask Migration Checklist

ToolTask subclasses extend `Microsoft.Build.Utilities.ToolTask` (not `TaskBase`) and spawn an external process whose CWD is independent of the build's project directory. **Relative paths in command-line arguments resolve against the spawned tool's CWD, not the project directory.**

- The `[MSBuildMultiThreadableTask]` attribute applies as normal.
- `IMultiThreadableTask` cannot be added directly because `ToolTask` already implements `ITask`. Implement `IMultiThreadableTask` on the subclass.

For the canonical list of override points to audit and absolutize (`ValidateParameters`, `GenerateCommandLineCommands`, `GenerateResponseFileCommands`, `GenerateFullPathToTool`, `ExecuteTool`, private helpers), see [`multithreaded-task-migration.md` § Where to Absolutize — ToolTask subclasses](./multithreaded-task-migration.md#where-to-absolutize-point-of-use-patterns).

**Test recipe:** Execute()/NRE smoke tests are insufficient for ToolTask migrations. Tests must verify `GenerateFullPathToTool()`, `GenerateCommandLineCommands()`, and `GenerateResponseFileCommands()` emit absolute paths under a decoy CWD. (PR #53121 — `GenerateFullPathToTool` was added as a separate follow-up commit `b3d3fc8` after initial omission.)
