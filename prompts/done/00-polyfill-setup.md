# Prompt: Create Polyfills for IMultiThreadableTask Infrastructure

## Skill Reference
Read the skill file at: `files/skills/multithreaded-task-migration.md` for full context.

## Task
Create polyfill files that provide the `IMultiThreadableTask`, `TaskEnvironment`, `AbsolutePath`, and `ITaskEnvironmentDriver` types for use in the SDK task projects. Also create a `TaskEnvironmentHelper` for unit tests.

## Repository
https://github.com/SimaTian/sdk/tree/main — clone and work on the `main` branch.

## Background
The MSBuild Framework package used by this project does not yet include the multithreading types. We need local polyfills following the same pattern as the existing `MSBuildMultiThreadableTaskAttribute` polyfill at `src/Tasks/Common/MSBuildMultiThreadableTaskAttribute.cs`, which is gated behind `#if NETFRAMEWORK`.

The reference implementations in dotnet/msbuild are:
- https://github.com/dotnet/msbuild/blob/main/src/Framework/IMultiThreadableTask.cs
- https://github.com/dotnet/msbuild/blob/main/src/Framework/TaskEnvironment.cs
- https://github.com/dotnet/msbuild/blob/main/src/Framework/PathHelpers/AbsolutePath.cs
- https://github.com/dotnet/msbuild/blob/main/src/Framework/ITaskEnvironmentDriver.cs

## Files to Create

### 1. `src/Tasks/Common/IMultiThreadableTask.cs`
- Namespace: `Microsoft.Build.Framework`
- Gate with `#if NETFRAMEWORK`
- Interface with single property: `TaskEnvironment TaskEnvironment { get; set; }`
- Extends `ITask`

### 2. `src/Tasks/Common/TaskEnvironment.cs`
- Namespace: `Microsoft.Build.Framework`
- Gate with `#if NETFRAMEWORK`
- Sealed class with:
  - Constructor taking `ITaskEnvironmentDriver driver`
  - `AbsolutePath ProjectDirectory { get; }`
  - `AbsolutePath GetAbsolutePath(string path)` — delegates to driver
  - `string? GetEnvironmentVariable(string name)` — delegates to driver
  - `IReadOnlyDictionary<string, string> GetEnvironmentVariables()` — delegates to driver
  - `void SetEnvironmentVariable(string name, string? value)` — delegates to driver
  - `ProcessStartInfo GetProcessStartInfo()` — delegates to driver

### 3. `src/Tasks/Common/AbsolutePath.cs`
- Namespace: `Microsoft.Build.Framework`
- Gate with `#if NETFRAMEWORK`
- Readonly struct implementing `IEquatable<AbsolutePath>`
- Properties: `string Value`, `string OriginalValue`
- Constructor: validates path is rooted (use `Path.IsPathRooted`)
- Internal constructor with `ignoreRootedCheck` flag
- Constructor with `(string path, AbsolutePath basePath)` for combining
- `static implicit operator string(AbsolutePath path)` → returns `Value`
- `GetCanonicalForm()` method using `Path.GetFullPath`
- Equality based on OS case sensitivity (`StringComparer.OrdinalIgnoreCase` on Windows, `Ordinal` on Linux)
- `ToString()` returns `Value`

### 4. `src/Tasks/Common/ITaskEnvironmentDriver.cs`
- Namespace: `Microsoft.Build.Framework`
- Gate with `#if NETFRAMEWORK`
- Internal interface with methods matching TaskEnvironment's delegation targets
- Extends `IDisposable`

### 5. `src/Tasks/Common/ProcessTaskEnvironmentDriver.cs`
- Namespace: `Microsoft.Build.Framework`  
- Gate with `#if NETFRAMEWORK`
- Internal class implementing `ITaskEnvironmentDriver`
- Wraps real process state: `Environment.GetEnvironmentVariable`, `Path.GetFullPath`, `Environment.CurrentDirectory`, etc.
- `GetAbsolutePath(path)`: if already rooted, return as-is; else combine with `ProjectDirectory` and return
- `GetProcessStartInfo()`: creates ProcessStartInfo with environment from this driver
- This is the equivalent of MSBuild's `MultiProcessTaskEnvironmentDriver`

### 6. `src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/TaskEnvironmentHelper.cs`
- Namespace: `Microsoft.NET.Build.Tasks.UnitTests` (or a shared namespace)
- NOT gated with #if — always available in test project
- Static class with:
  - `CreateForTest()` — creates TaskEnvironment with ProcessTaskEnvironmentDriver using `Directory.GetCurrentDirectory()` as ProjectDirectory
  - `CreateForTest(string projectDirectory)` — creates TaskEnvironment with specified ProjectDirectory

## Verification

### The csproj files should NOT need changes
The `Microsoft.NET.Build.Tasks.csproj` already includes `<Compile Include="..\Common\**\*.cs" LinkBase="Common" />` so new files in Common/ are auto-included.

### Build must pass
```bash
dotnet build src/Tasks/Microsoft.NET.Build.Tasks/Microsoft.NET.Build.Tasks.csproj
dotnet build src/Tasks/Microsoft.NET.Build.Extensions.Tasks/Microsoft.NET.Build.Extensions.Tasks.csproj
```

### Existing tests must pass
```bash
dotnet test src/Tasks/Microsoft.NET.Build.Tasks.UnitTests/Microsoft.NET.Build.Tasks.UnitTests.csproj
```

## Acceptance Criteria
- [ ] All 5 polyfill files created in `src/Tasks/Common/` with `#if NETFRAMEWORK` guards
- [ ] `TaskEnvironmentHelper.cs` created in test project (no #if guard)
- [ ] `ProcessTaskEnvironmentDriver` provides a working implementation that wraps real process state
- [ ] Both task projects build successfully (net472 + net SDK TFM)
- [ ] All existing unit tests pass
- [ ] The polyfill types match the API surface of the dotnet/msbuild originals (at minimum the public members used by tasks)
- [ ] `AbsolutePath` is implicitly convertible to `string` so it works seamlessly with `File.*` APIs
