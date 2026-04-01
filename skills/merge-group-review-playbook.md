# Skill: Merge Group Review & Pipeline Fix Playbook

## Purpose

This skill encodes all findings, reviewer expectations, pipeline failure patterns, and defensive coding
practices learned from reviewing and fixing merge groups 1–4 (PRs #52935, #52936, #52937, #52938) against
`dotnet/sdk`. Use this when reviewing or fixing merge groups 5–11.

---

## 1. Pipeline Failure Taxonomy (Ranked by Frequency)

### 1.1 TaskEnvironment NullReferenceException (Most Common)

**Symptom**: `System.NullReferenceException` in `ExecuteCore()` or any method that calls
`TaskEnvironment.GetAbsolutePath(...)`.

**Root Cause**: On .NET Core, the `TaskEnvironment` property is a bare auto-property:
```csharp
public TaskEnvironment TaskEnvironment { get; set; }
```
There is **no lazy-init fallback** — unlike NETFRAMEWORK where:
```csharp
private TaskEnvironment _taskEnvironment;
public TaskEnvironment TaskEnvironment
{
    get => _taskEnvironment ??= TaskEnvironmentDefaults.Create();
    set => _taskEnvironment = value;
}
```
Tests that instantiate tasks without explicitly setting `TaskEnvironment` get NRE.

**Fix**: In every test that creates a migrated task, add:
```csharp
var task = new SomeTask
{
    BuildEngine = new MockBuildEngine(),
    TaskEnvironment = _env.TaskEnvironment,  // if using TaskTestEnvironment
    // OR
    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),  // simple default
    // OR
    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDirectory),  // specific dir
};
```

**Pre-flight check**: Before pushing, grep the test file for every `new <TaskName>` or `new <TaskName>()`
and verify each has a `TaskEnvironment =` assignment. Tasks that DON'T implement `IMultiThreadableTask`
(e.g., `ShowPreviewMessage` on mg2) must NOT get this assignment — it causes CS0117.

**Affected merge groups**: mg1 (4 tasks), mg2 (2 tasks), mg3 (ResolvePackageAssets), mg6 (8 ProduceContentAssets instantiations), mg7 (SelectRuntimeIdentifierSpecificItems).

---

### 1.2 Cross-Platform Path Separator Failures (Second Most Common)

**Symptom**: Test passes on Windows, fails on Linux/macOS. Assertion like:
- `Expected "packages/NuGet.Common..." but was "packages\NuGet.Common..."`
- `Expected True but was False` (file not found)

**Root Cause**: Hardcoded backslash `\` in test paths. On Linux, backslash is a literal filename
character, NOT a path separator.

**Three failure modes**:

| Pattern | Problem | Fix |
|---------|---------|-----|
| `Path.Combine("refs", "refs\\file.dll")` | `\` not recognized as separator on Linux → wrong path | Use `Path.Combine("refs", "file.dll")` |
| `"packages\\NuGet.Common"` in assertion | `TaskItem` normalizes `\` to `/` on Linux | Split into cross-platform `[Theory]` (forward slash) + `[WindowsOnlyFact]` (backslash) |
| Hardcoded `$"refs\\{name}"` as relative path | Not cross-platform | Use `Path.Combine("refs", name)` |

**Pre-flight check**: `grep -rn '\\\\' <test-file>` — any escaped backslash in a path literal is suspicious.

**Affected merge groups**: mg3 (GivenAGetDependsOnNETStandard), mg4 (GivenACollatePackageDownloads).

---

### 1.3 GetCanonicalForm() Compilation Error (Rare but Critical)

**Symptom**: `error CS1061: 'AbsolutePath' does not contain a definition for 'GetCanonicalForm'`

**Root Cause**: `GetCanonicalForm()` exists in the `#if NETFRAMEWORK` polyfill
(`src/Tasks/Common/AbsolutePath.cs`) but is `internal` in the real MSBuild `AbsolutePath` type shipped
via NuGet. On .NET Core builds, the real type is used and `GetCanonicalForm()` is not accessible.

**Fix**: Replace:
```csharp
// BROKEN on .NET Core:
return TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm();
```
With:
```csharp
// Works everywhere — Path.GetFullPath resolves ".." segments:
return Path.GetFullPath(new AbsolutePath(path, absProjectDir));
// OR simply:
return Path.GetFullPath(TaskEnvironment.GetAbsolutePath(path));
```

**Key ruling from Jan Provaznik**: The `AbsolutePath` constructor does NOT normalize — it only
does `Path.Combine`. Normalization (resolving `..` segments) is the **caller's responsibility**.

**Affected merge groups**: mg1 (ResolvePackageDependencies).

---

### 1.4 CS0117/CS0053 Build Errors (Task Not Migrated on This Branch)

**Symptom**: `error CS0117: 'SomeTask' does not contain a definition for 'TaskEnvironment'`

**Root Cause**: The task exists on the branch but was NOT migrated to `IMultiThreadableTask` on that
particular merge group. Assigning `TaskEnvironment` in a test causes a compilation error.

**Fix**: Check whether the task actually implements `IMultiThreadableTask` on that branch. If not,
remove the `TaskEnvironment =` line.

**Pre-flight check**: Verify each task in `GivenTasksUseAbsolutePaths.cs` (or similar) actually has
the `TaskEnvironment` property before assigning it.

**Affected merge groups**: mg2 (ShowPreviewMessage).

---

### 1.5 Unused `using` Directives

**Symptom**: `warning CS8019` turned into error by `TreatWarningsAsErrors=True` (global).

**Fix**: Remove unused `using` statements. Run `dotnet format` after changes.

**Affected merge groups**: mg4 (TaskEnvironmentHelperTests).

---

### 1.6 Absolutization Leak to Outputs

**Symptom**: Task output metadata (SetMetadata, FilesWritten, Output properties) contains absolutized paths instead of preserving the original input form. Behavioral change — downstream MSBuild targets that depend on relative paths break.

**Root cause**: Casting `AbsolutePath` to `string` in `ExecuteCore()` (via `(string)TaskEnvironment.GetAbsolutePath(...)` or implicit conversion), then passing the string to both file I/O and output metadata. The cast loses `OriginalValue`.

**Fix**: 
1. Move absolutization inside the method performing file I/O (not ExecuteCore)
2. Keep `AbsolutePath` objects — use `.Value` for file I/O, original property for outputs
3. For fields that serve dual purpose (I/O + output), use `AbsolutePath?` type and compute original-form outputs from the original property

**Detection**: Search for `(string)TaskEnvironment.GetAbsolutePath` or `string x = TaskEnvironment.GetAbsolutePath` — both discard `OriginalValue`. Also check all `SetMetadata` and `_filesWritten.Add` calls to verify they don't use absolutized variables.

**Affected merge groups**: mg2 (GenerateDepsFile, ResolveAppHosts).

---

## 2. Reviewer Expectations (Key Reviewers)

### 2.1 JanProvaznik (Primary Reviewer)

| Preference | Example |
|-----------|---------|
| **Typed `AbsolutePath` at call sites** | `AbsolutePath absPath = TaskEnvironment.GetAbsolutePath(path);` not `var absPath = ...` |
| **No normalization in AbsolutePath** | Constructor only does `Path.Combine` — caller normalizes |
| **Remove "nonsense tests"** | Delete attribute-only verification tests, multi-threading interface checks |
| **`[Theory]` with precomputed expected values** | Don't compare multiprocess vs multithreaded — assert against known expected value |
| **Use `TaskTestEnvironment`** | Not manual `Path.GetTempPath()` + `Directory.CreateDirectory()` |
| **Direct `TaskEnvironment` assignment** | `task.TaskEnvironment = env.TaskEnvironment;` not reflection |
| **Explanatory comments on non-absolutized properties** | "not absolutized because it's never used as a full path" |
| **`AbsolutePath` constructor is NOT for normalization** | "no" (terse rejection of normalization comments) |

### 2.2 AR-May

| Preference | Example |
|-----------|---------|
| **Dual-mode parity tests** | Run task in both multiprocess and multithreaded environments, assert identical outputs |
| **No attribute-verification tests** | "I think this kind of test is not required" |
| **Use `AbsolutePath` constructor for path combining** | `new AbsolutePath(path, absProjectDir)` |
| **Behavioral tests over structural tests** | Test actual task output, not interface shape |
| **Keep `AbsolutePath` objects — don't cast to string** | Absolutize inside the method doing I/O, not in ExecuteCore |
| **Use `OriginalValue` for outputs** | Output metadata and FilesWritten must preserve original path form |
| **No absolutization outside the consuming method** | "There is no reason to compute absolute paths outside this function" |

### 2.3 copilot-pull-request-reviewer (Automated)

Common automated suggestions that are often valid:
- `UseShellExecute = false` on NETFRAMEWORK `ProcessStartInfo`
- Inconsistent accessibility (`internal TaskEnvironment` as `public` property on net472)
- Null/empty path handling (`Path.GetDirectoryName()` returning null)
- Unused usings

---

## 3. Code Patterns Cheat Sheet

### 3.1 TaskEnvironment Property (NETFRAMEWORK vs .NET Core)

```csharp
// Pattern B task: IMultiThreadableTask implementation
#if NETFRAMEWORK
    private TaskEnvironment _taskEnvironment;
    public TaskEnvironment TaskEnvironment
    {
        get => _taskEnvironment ??= TaskEnvironmentDefaults.Create();
        set => _taskEnvironment = value;
    }
#else
    public TaskEnvironment TaskEnvironment { get; set; }
#endif
```

Some tasks use explicit interface implementation instead (when `TaskEnvironment` is `internal` on NETFRAMEWORK):
```csharp
#if NETFRAMEWORK
    TaskEnvironment IMultiThreadableTask.TaskEnvironment { get; set; }
    private TaskEnvironment TaskEnvironment => ((IMultiThreadableTask)this).TaskEnvironment;
#else
    public TaskEnvironment TaskEnvironment { get; set; }
#endif
```

### 3.2 Path Resolution

```csharp
// Before (CWD-dependent):
string absPath = Path.GetFullPath(relativePath);

// After (TaskEnvironment-based):
AbsolutePath absPath = TaskEnvironment.GetAbsolutePath(relativePath);
// absPath implicitly converts to string when needed

// If normalization needed (resolving ".." segments):
string normalized = Path.GetFullPath(TaskEnvironment.GetAbsolutePath(relativePath));
```

### 3.3 Test Setup with TaskTestEnvironment

```csharp
using var env = new TaskTestEnvironment();
// env.ProjectDirectory — where project files live
// env.SpawnDirectory — CWD is set here (tests must NOT depend on it)
// env.TaskEnvironment — pre-configured TaskEnvironment pointing at ProjectDirectory

var task = new SomeTask
{
    BuildEngine = new MockBuildEngine(),
    TaskEnvironment = env.TaskEnvironment,
    SomeRelativePath = "subdir/file.txt",
};

env.CreateProjectFile("subdir/file.txt", "content");
task.Execute().Should().BeTrue();
```

### 3.4 ProcessTaskEnvironmentDriver (NETFRAMEWORK)

```csharp
var startInfo = new ProcessStartInfo
{
    WorkingDirectory = _projectDirectory.Value,
    UseShellExecute = false,  // CRITICAL: Must be false for EnvironmentVariables to work
};
```

---

## 4. Pre-Push Checklist

Run this checklist before pushing any merge group fix:

- [ ] **TaskEnvironment assigned in every test instantiation** — grep for `new <TaskName>` and verify
- [ ] **Task actually implements IMultiThreadableTask** — don't assign TaskEnvironment to non-migrated tasks
- [ ] **No hardcoded backslashes in path literals** — use `Path.Combine()` for all path construction
- [ ] **No `GetCanonicalForm()` calls** — use `Path.GetFullPath()` instead
- [ ] **No unused `using` directives** — `TreatWarningsAsErrors` will catch them
- [ ] **`AbsolutePath` typed explicitly** at `GetAbsolutePath()` call sites (not `var`)
- [ ] **Test uses `TaskTestEnvironment`** — not manual temp dir management
- [ ] **No attribute-only tests** — remove or replace with behavioral tests
- [ ] **`[Theory]` with precomputed values** — not multiprocess-vs-multithreaded comparison
- [ ] **Cross-platform path assertions** — use `[WindowsOnlyFact]` for backslash tests
- [ ] **Explanatory comments** on properties that are intentionally NOT absolutized
- [ ] **`UseShellExecute = false`** in any `ProcessStartInfo` under NETFRAMEWORK
- [ ] **Injection completeness verified** — if any class was refactored to accept delegates/interfaces, verify every method uses them (no leaked static calls to `Environment.*`, no library bypasses like `DotNetReferenceAssembliesPathResolver.Resolve()`)
- [ ] **No absolutization leak to outputs** — verify no `(string)TaskEnvironment.GetAbsolutePath(...)` casts; check all SetMetadata/FilesWritten calls use original properties, not absolutized variables

---

## 5. Transitive Forbidden API Usage

The hardest bugs to catch: external library methods that bypass `TaskEnvironment`.

**Known instance**: `DotNetReferenceAssembliesPathResolver.Resolve()` from `Microsoft.Extensions.DependencyModel`
internally calls `Environment.GetEnvironmentVariable("DOTNET_REFERENCE_ASSEMBLIES_PATH")` and `Directory.Exists()`.
This bypasses `TaskEnvironment` entirely.

**Remediation options**:
1. Inline the library method's logic using `taskEnvironment.GetEnvironmentVariable(...)` — **preferred and required when the delegate was specifically introduced to replace the static call**
2. Document as known limitation with `// TODO: pre-existing issue — library bypasses TaskEnvironment` — only acceptable when inlining is complex and the env var is unlikely to differ per-task
3. Do NOT modify the runtime library — that's a different repo's concern

**Severity upgrade**: When a class was refactored to accept an injected delegate specifically to replace a static API, and a method in that class still calls the old static API, this is a **bug** — not a TODO. The refactoring claim is that all reads go through the delegate. A leaked static call violates that claim. Fix before merge.

**Per SimaTian's observation**: Automated agents consistently miss transitive violations through external calls.
Manual review of every external method invocation in migrated code is required.

---

## 6. Git Workflow Notes

- **Remote branches get force-pushed** by CI and other processes — always `git fetch` before applying fixes
- **Rebase often fails** when remote has been force-pushed — use `git reset --hard origin/<branch>` then re-apply
- **Each merge group is a separate branch** off main (not stacked) — fixes in one don't affect others
- **Format commit after code commit** — then add format commit SHA to `.git-blame-ignore-revs`

---

## 7. Common Fix Templates

### Fix: TaskEnvironment NRE in existing test file

```diff
 var task = new SomeTask
 {
     BuildEngine = new MockBuildEngine(),
+    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
     InputProperty = "value",
 };
```

### Fix: Cross-platform path in test

```diff
-var relativePath = $"refs\\{assemblyFileName}";
+var relativePath = Path.Combine("refs", assemblyFileName);
```

### Fix: Backslash assertion split

```diff
-[Theory]
-[InlineData("packages\\NuGet.Common")]
-[InlineData("packages/NuGet.Common")]
-public void PathItemSpec_PreservesFormat(string pathSpec)
+[Theory]
+[InlineData("packages/NuGet.Common")]
+public void PathItemSpec_PreservesFormat(string pathSpec)
+// ...
+
+[WindowsOnlyFact]
+public void PathItemSpec_PreservesBackslashOnWindows()
```

### Fix: GetCanonicalForm replacement

```diff
-return TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm();
+return Path.GetFullPath(new AbsolutePath(path, absProjectDir));
```

---

## 8. Failure-to-Fix Mapping (Quick Reference)

| Error Pattern | Root Cause | Fix |
|---|---|---|
| `NullReferenceException` in `ExecuteCore` | Missing `TaskEnvironment` in test | Add `TaskEnvironment = ...` to object initializer |
| `CS1061: 'AbsolutePath' does not contain 'GetCanonicalForm'` | Polyfill-only method called on .NET Core | Use `Path.GetFullPath()` |
| `CS0117: 'X' does not contain 'TaskEnvironment'` | Task not migrated on this branch | Remove `TaskEnvironment =` assignment |
| `Expected "packages/..." but was "packages\..."` | Backslash normalization on Linux | Split into cross-platform + WindowsOnly test |
| `Expected True` for file existence | Backslash in `Path.Combine` on Linux | Use `Path.Combine("dir", "file")` not `"dir\\file"` |
| `CS8019` unused using (as error) | TreatWarningsAsErrors=True | Remove the using |
| `FileNotFoundException` in Helix | Relative path resolved against wrong CWD | Absolutize via `TaskEnvironment.GetAbsolutePath()` |

---

## 9. Review Agent Instructions

When spawning agents to review merge groups 5–11, provide this context:

1. **Fetch the PR diff** and list all changed files
2. **For each migrated task**: verify Pattern A (attribute-only) or Pattern B (interface-based) is correct
3. **For each test file**: verify TaskEnvironment is set on every task instantiation
4. **Scan for forbidden patterns**: `GetCanonicalForm()`, hardcoded `\\`, unused usings, `var` at GetAbsolutePath sites
5. **Check ProcessTaskEnvironmentDriver**: `UseShellExecute = false` present
6. **Check for transitive violations**: trace external library calls from migrated methods
7. **Verify cross-platform compatibility**: no hardcoded path separators in assertions
8. **Check reviewer comments history**: similar issues to mg1–4 are likely
