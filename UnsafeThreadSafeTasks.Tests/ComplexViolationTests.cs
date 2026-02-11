using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;

using Broken = UnsafeThreadSafeTasks.ComplexViolations;
using Fixed = FixedThreadSafeTasks.ComplexViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class ComplexViolationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockBuildEngine _engine;

        public ComplexViolationTests()
        {
            _tempDir = TestHelper.CreateNonCwdTempDirectory();
            _engine = new MockBuildEngine();
        }

        public void Dispose()
        {
            TestHelper.CleanupTempDirectory(_tempDir);
        }

        #region DeepCallChainPathResolve

        [Fact]
        public void DeepCallChain_Broken_ShouldResolveToProjectDir()
        {
            File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "// test");

            var task = new Broken.DeepCallChainPathResolve
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputFiles = new ITaskItem[] { new TaskItem("test.txt") }
            };

            bool result;
            try
            {
                result = task.Execute();
            }
            catch
            {
                result = false;
            }

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.True(result);
            Assert.NotEmpty(task.ProcessedFiles);
            string resolved = task.ProcessedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DeepCallChain_Fixed_ShouldResolveToProjectDir()
        {
            File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "// test");

            var task = new Fixed.DeepCallChainPathResolve
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputFiles = new ITaskItem[] { new TaskItem("test.txt") }
            };

            task.Execute();

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.NotEmpty(task.ProcessedFiles);
            string resolved = task.ProcessedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region BaseClassHidesViolation

        [Fact]
        public void BaseClass_Broken_ShouldResolveToProjectDir()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.cs"), "// src");

            var task = new Broken.DerivedFileProcessor
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                Sources = new ITaskItem[] { new TaskItem("source.cs") }
            };

            task.Execute();

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.NotEmpty(task.ResolvedSources);
            string resolved = task.ResolvedSources[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BaseClass_Fixed_ShouldResolveToProjectDir()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.cs"), "// src");

            var task = new Fixed.DerivedFileProcessor
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                Sources = new ITaskItem[] { new TaskItem("source.cs") }
            };

            task.Execute();

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.NotEmpty(task.ResolvedSources);
            string resolved = task.ResolvedSources[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region UtilityClassViolation

        [Fact]
        public void UtilityClass_Broken_ShouldResolveToProjectDir()
        {
            var task = new Broken.OutputDirectoryResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                OutputDirectory = "bin"
            };

            task.Execute();

            // Assert CORRECT behavior: resolved output dir should contain tempDir
            Assert.Contains(_tempDir, task.ResolvedOutputDirectory, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void UtilityClass_Fixed_ShouldResolveToProjectDir()
        {
            var task = new Fixed.OutputDirectoryResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                OutputDirectory = "bin"
            };

            task.Execute();

            // Assert CORRECT behavior: resolved output dir should contain tempDir
            Assert.Contains(_tempDir, task.ResolvedOutputDirectory, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region AsyncDelegateViolation

        [Fact]
        public void AsyncDelegate_Broken_ShouldResolveToProjectDir()
        {
            string testFile = Path.Combine(_tempDir, "data.txt");
            File.WriteAllText(testFile, "hello");

            var task = new Broken.AsyncDelegateViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SourceFiles = new ITaskItem[] { new TaskItem("data.txt") },
                ParallelProcessing = false
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: file should be found and processed
            Assert.True(result);
            Assert.NotEmpty(task.ProcessedFiles);
            string fullPath = task.ProcessedFiles[0].GetMetadata("ResolvedFullPath");
            Assert.StartsWith(_tempDir, fullPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AsyncDelegate_Fixed_ShouldResolveToProjectDir()
        {
            string testFile = Path.Combine(_tempDir, "data.txt");
            File.WriteAllText(testFile, "hello world");

            var task = new Fixed.AsyncDelegateViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SourceFiles = new ITaskItem[] { new TaskItem("data.txt") },
                ParallelProcessing = false
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: file should be found and processed
            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}. Warnings: {string.Join("; ", _engine.Warnings.Select(e => e.Message))}");
            Assert.NotEmpty(task.ProcessedFiles);
            string fullPath = task.ProcessedFiles[0].GetMetadata("ResolvedFullPath");
            Assert.StartsWith(_tempDir, fullPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region EventHandlerViolation

        [Fact]
        public void EventHandler_Broken_ShouldResolveToProjectDir()
        {
            string watchDir = Path.Combine(_tempDir, "watch");
            Directory.CreateDirectory(watchDir);
            File.WriteAllText(Path.Combine(watchDir, "file1.txt"), "test");

            var task = new Broken.EventHandlerViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                WatchDirectory = "watch",
                FilePatterns = new[] { "*.txt" }
            };

            task.Execute();

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.NotEmpty(task.ChangedFiles);
            string resolved = task.ChangedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EventHandler_Fixed_ShouldResolveToProjectDir()
        {
            string watchDir = Path.Combine(_tempDir, "watch");
            Directory.CreateDirectory(watchDir);
            File.WriteAllText(Path.Combine(watchDir, "file1.txt"), "test");

            var task = new Fixed.EventHandlerViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                WatchDirectory = "watch",
                FilePatterns = new[] { "*.txt" }
            };

            task.Execute();

            // Assert CORRECT behavior: resolved paths should contain tempDir
            Assert.NotEmpty(task.ChangedFiles);
            string resolved = task.ChangedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region ThreadPoolViolation

        [Fact]
        public void ThreadPool_Broken_ShouldUseTaskEnvironmentForEnvVars()
        {
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);

            string sdkDir = Path.Combine(_tempDir, "sdk", "tool1");
            Directory.CreateDirectory(sdkDir);

            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

            var workItem = new TaskItem("tool1");
            workItem.SetMetadata("Category", "ConfigPath");

            var task = new Broken.ThreadPoolViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                WorkItems = new ITaskItem[] { workItem }
            };

            task.Execute();

            // Assert CORRECT behavior: task should use TaskEnvironment env vars
            Assert.NotEmpty(task.CompletedItems);
            string resolvedPath = task.CompletedItems[0].GetMetadata("ResolvedPath");
            Assert.Contains(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ThreadPool_Fixed_ShouldUseTaskEnvironmentForEnvVars()
        {
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);

            string sdkDir = Path.Combine(_tempDir, "sdk", "tool1");
            Directory.CreateDirectory(sdkDir);

            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

            var workItem = new TaskItem("tool1");
            workItem.SetMetadata("Category", "ConfigPath");

            var task = new Fixed.ThreadPoolViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                WorkItems = new ITaskItem[] { workItem }
            };

            task.Execute();

            // Assert CORRECT behavior: task should use TaskEnvironment env vars
            Assert.NotEmpty(task.CompletedItems);
            string resolvedPath = task.CompletedItems[0].GetMetadata("ResolvedPath");
            Assert.Contains(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region LazyInitializationViolation

        [Fact]
        public void LazyInitialization_Broken_ShouldUseTaskEnvironment()
        {
            // The broken task uses Environment.GetEnvironmentVariable in constructor Lazy lambdas
            // instead of TaskEnvironment.GetEnvironmentVariable in Execute().
            // Create a config file only accessible via task-scoped env vars.

            var nugetDir = Path.Combine(_tempDir, "nuget-packages");
            var cacheDir = Path.Combine(nugetDir, "cache");
            Directory.CreateDirectory(cacheDir);

            // Write a dependency config file with a known dependency
            File.WriteAllText(Path.Combine(cacheDir, "dependency-config.json"),
                "TestDep=1.0.0");

            // Create the SDK location where the dependency can be resolved
            var sdkDir = Path.Combine(_tempDir, "dotnet-sdk");
            Directory.CreateDirectory(Path.Combine(sdkDir, "packs", "TestDep", "1.0.0"));

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);
            // Set env vars ONLY in TaskEnvironment (not process environment)
            taskEnv.SetEnvironmentVariable("NUGET_PACKAGES", nugetDir);
            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", sdkDir);

            var task = new Broken.LazyInitializationViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                ConfigurationFile = "config.json",
                TargetFramework = "net8.0"
            };

            bool result = task.Execute();

            // The broken task reads NUGET_PACKAGES from Environment (process-level)
            // instead of TaskEnvironment, so it won't find our config file.
            // If working correctly (using TaskEnvironment), it should resolve the dependency.
            Assert.True(result);
            Assert.NotEmpty(task.ResolvedDependencies);
        }

        [Fact]
        public void LazyInitialization_Fixed_ShouldUseTaskEnvironment()
        {
            var nugetDir = Path.Combine(_tempDir, "nuget-packages");
            var cacheDir = Path.Combine(nugetDir, "cache");
            Directory.CreateDirectory(cacheDir);

            File.WriteAllText(Path.Combine(cacheDir, "dependency-config.json"),
                "TestDep=1.0.0");

            var sdkDir = Path.Combine(_tempDir, "dotnet-sdk");
            Directory.CreateDirectory(Path.Combine(sdkDir, "packs", "TestDep", "1.0.0"));

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);
            taskEnv.SetEnvironmentVariable("NUGET_PACKAGES", nugetDir);
            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", sdkDir);

            var task = new Fixed.LazyInitializationViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                ConfigurationFile = "config.json",
                TargetFramework = "net8.0"
            };

            bool result = task.Execute();

            // Fixed task uses TaskEnvironment env vars, so it should find and resolve the dependency
            Assert.True(result);
            Assert.NotEmpty(task.ResolvedDependencies);
        }

        #endregion

        #region LinqPipelineViolation

        [Fact]
        public void LinqPipeline_Broken_ShouldResolveToProjectDir()
        {
            var item = new TaskItem("ext-ref.dll");
            item.SetMetadata("Category", "ExternalReference");

            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = _tempDir };
            var task = new Broken.LinqPipelineViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                InputItems = new ITaskItem[] { item }
            };

            task.Execute();

            // The broken task uses Path.GetFullPath() for ExternalReference items
            // instead of TaskEnvironment.GetAbsolutePath(). The correct implementation
            // should call GetAbsolutePath for EVERY item including ExternalReferences.
            // GetCanonicalForm (called in NormalizePaths) internally calls GetAbsolutePath once,
            // and ResolveGroupPaths should call GetAbsolutePath again for ExternalReference items.
            Assert.NotEmpty(task.FilteredItems);
            Assert.True(taskEnv.GetAbsolutePathCallCount >= 2,
                $"Task should call GetAbsolutePath for ExternalReference resolution (called {taskEnv.GetAbsolutePathCallCount} times, expected >= 2)");
        }

        [Fact]
        public void LinqPipeline_Fixed_ShouldResolveToProjectDir()
        {
            var item = new TaskItem("ext-ref.dll");
            item.SetMetadata("Category", "ExternalReference");

            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = _tempDir };
            var task = new Fixed.LinqPipelineViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                InputItems = new ITaskItem[] { item }
            };

            task.Execute();

            // Fixed task uses TaskEnvironment.GetAbsolutePath for all items including ExternalReferences
            Assert.NotEmpty(task.FilteredItems);
            Assert.True(taskEnv.GetAbsolutePathCallCount >= 2,
                $"Fixed task should call GetAbsolutePath for ExternalReference resolution (called {taskEnv.GetAbsolutePathCallCount} times, expected >= 2)");
        }

        #endregion

        #region DictionaryCacheViolation

        [Fact]
        public void DictionaryCache_Broken_ShouldResolveToProjectDir()
        {
            string libDir = Path.Combine(_tempDir, "lib", "net8.0");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "MyAssembly.dll"), "fake-dll");

            var task = new Broken.DictionaryCacheViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                AssemblyReferences = new ITaskItem[] { new TaskItem("MyAssembly") }
            };

            bool result;
            try
            {
                result = task.Execute();
            }
            catch
            {
                result = false;
            }

            // Assert CORRECT behavior: assembly should be found and resolved under tempDir
            Assert.True(result);
            Assert.NotEmpty(task.ResolvedReferences);
            string resolvedPath = task.ResolvedReferences[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DictionaryCache_Fixed_ShouldResolveToProjectDir()
        {
            string libDir = Path.Combine(_tempDir, "lib", "net8.0");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "MyAssembly.dll"), "fake-dll");

            var task = new Fixed.DictionaryCacheViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                AssemblyReferences = new ITaskItem[] { new TaskItem("MyAssembly") }
            };

            task.Execute();

            // Assert CORRECT behavior: assembly should be found and resolved under tempDir
            Assert.NotEmpty(task.ResolvedReferences);
            string resolvedPath = task.ResolvedReferences[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region ProjectFileAnalyzer

        [Fact]
        public void ProjectFileAnalyzer_Broken_ShouldResolveToProjectDir()
        {
            string projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Lib\Lib.csproj"" />
  </ItemGroup>
</Project>";

            string parentProjDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(parentProjDir);
            string projFile = Path.Combine(parentProjDir, "App.csproj");
            File.WriteAllText(projFile, projectContent);

            string libProjContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Common\Common.csproj"" />
  </ItemGroup>
</Project>";
            string libDir = Path.Combine(_tempDir, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), libProjContent);

            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = _tempDir };
            var task = new Broken.ProjectFileAnalyzer
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                ProjectFilePath = Path.Combine("src", "App.csproj"),
                ResolveTransitive = true
            };

            task.Execute();

            // The broken task uses Path.GetFullPath() for transitive reference resolution
            // instead of TaskEnvironment.GetCanonicalForm(). Verify GetCanonicalForm was called
            // for transitive resolution (it should be called at least once for the transitive ref).
            Assert.NotEmpty(task.AnalyzedReferences);
            var transitiveRef = task.AnalyzedReferences.FirstOrDefault(
                r => r.GetMetadata("IsTransitive") == "True");
            Assert.NotNull(transitiveRef);
            // The broken task calls Path.GetFullPath directly for transitive refs,
            // so GetCanonicalForm is NOT called for those paths
            Assert.True(taskEnv.GetCanonicalFormCallCount > 0,
                "Task should use TaskEnvironment.GetCanonicalForm() for transitive reference resolution");
        }

        [Fact]
        public void ProjectFileAnalyzer_Fixed_ShouldResolveToProjectDir()
        {
            // Create App.csproj referencing Lib.csproj (sibling directory)
            string projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Lib\Lib.csproj"" />
  </ItemGroup>
</Project>";

            string parentProjDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(parentProjDir);
            string projFile = Path.Combine(parentProjDir, "App.csproj");
            File.WriteAllText(projFile, projectContent);

            // Create Lib.csproj referencing Common.csproj (transitive)
            string libProjContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Common\Common.csproj"" />
  </ItemGroup>
</Project>";
            string libDir = Path.Combine(_tempDir, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), libProjContent);

            // Use the src directory as ProjectDirectory so "..\Lib\Lib.csproj" resolves correctly
            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = parentProjDir };
            var task = new Fixed.ProjectFileAnalyzer
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                ProjectFilePath = "App.csproj",
                ResolveTransitive = true
            };

            task.Execute();

            // Assert CORRECT behavior: fixed task uses TaskEnvironment methods
            Assert.NotEmpty(task.AnalyzedReferences);
            Assert.True(taskEnv.GetAbsolutePathCallCount > 0 || taskEnv.GetCanonicalFormCallCount > 0,
                "Fixed task should use TaskEnvironment for path resolution");
        }

        #endregion

        #region NuGetPackageValidator

        [Fact]
        public void NuGetPackageValidator_Broken_ShouldResolveToProjectDir()
        {
            // Create packages ONLY in the task-scoped global packages location.
            // The broken task uses Environment.GetFolderPath(UserProfile) which gives
            // the real user profile, while the fixed task uses TaskEnvironment.GetEnvironmentVariable("USERPROFILE").
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);

            // Ensure NUGET_PACKAGES is not set (force fallback to user profile path)
            taskEnv.SetEnvironmentVariable("NUGET_PACKAGES", null);
            // Set USERPROFILE in TaskEnvironment to our temp dir
            taskEnv.SetEnvironmentVariable("USERPROFILE", _tempDir);
            taskEnv.SetEnvironmentVariable("HOME", _tempDir);

            // Create the package under the task-scoped global packages folder
            string globalPkgDir = Path.Combine(_tempDir, ".nuget", "packages");
            string packageDir = Path.Combine(globalPkgDir, "fakepackage", "1.0.0");
            Directory.CreateDirectory(packageDir);
            string libDir = Path.Combine(packageDir, "lib");
            Directory.CreateDirectory(libDir);

            // Use a non-existent local packages dir so the package is only found in global
            var pkg = new TaskItem("FakePackage");
            pkg.SetMetadata("Version", "1.0.0");

            var task = new Broken.NuGetPackageValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                PackagesDirectory = "nonexistent-packages",
                PackagesToValidate = new ITaskItem[] { pkg }
            };

            task.Execute();

            // The broken task falls back to Environment.GetFolderPath(UserProfile)
            // for the global packages folder, which won't match _tempDir.
            // The package should be found and validated if using TaskEnvironment.
            Assert.NotEmpty(task.ValidatedPackages);
        }

        [Fact]
        public void NuGetPackageValidator_Fixed_ShouldResolveToProjectDir()
        {
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);

            // Ensure NUGET_PACKAGES is not set (force fallback to user profile path)
            taskEnv.SetEnvironmentVariable("NUGET_PACKAGES", null);
            // Set USERPROFILE in TaskEnvironment to our temp dir
            taskEnv.SetEnvironmentVariable("USERPROFILE", _tempDir);
            taskEnv.SetEnvironmentVariable("HOME", _tempDir);

            // Create the package under the task-scoped global packages folder
            string globalPkgDir = Path.Combine(_tempDir, ".nuget", "packages");
            string packageDir = Path.Combine(globalPkgDir, "fakepackage", "1.0.0");
            Directory.CreateDirectory(packageDir);
            string libDir = Path.Combine(packageDir, "lib");
            Directory.CreateDirectory(libDir);

            var pkg = new TaskItem("FakePackage");
            pkg.SetMetadata("Version", "1.0.0");

            var task = new Fixed.NuGetPackageValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                PackagesDirectory = "nonexistent-packages",
                PackagesToValidate = new ITaskItem[] { pkg }
            };

            task.Execute();

            // Fixed task uses TaskEnvironment.GetEnvironmentVariable("USERPROFILE")
            // to find the global packages folder, so it should find the package.
            Assert.NotEmpty(task.ValidatedPackages);
        }

        #endregion

        #region AssemblyReferenceResolver

        [Fact]
        public void AssemblyReferenceResolver_Broken_ShouldResolveToProjectDir()
        {
            string binDir = Path.Combine(_tempDir, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "TestLib.dll"), "fake");

            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = _tempDir };
            // Set DOTNET_ROOT to trigger the runtime pack resolution code path
            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

            var task = new Broken.AssemblyReferenceResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                References = new ITaskItem[] { new TaskItem("TestLib") },
                TargetFramework = "net8.0",
                RuntimeIdentifier = "win-x64"
            };

            task.Execute();

            // The broken task uses Path.GetFullPath() for runtime pack resolution
            // instead of TaskEnvironment.GetAbsolutePath(). When DOTNET_ROOT is set,
            // the fixed task should call GetAbsolutePath for the runtime pack path.
            Assert.NotEmpty(task.ResolvedReferences);
            // Check that the runtime pack path was resolved via TaskEnvironment.GetAbsolutePath
            bool runtimePackResolvedViaTaskEnv = taskEnv.GetAbsolutePathArgs.Any(arg =>
                arg.Contains("packs") && arg.Contains("Microsoft.NETCore.App.Runtime"));
            Assert.True(runtimePackResolvedViaTaskEnv,
                "Task should use TaskEnvironment.GetAbsolutePath() for runtime pack path resolution");
        }

        [Fact]
        public void AssemblyReferenceResolver_Fixed_ShouldResolveToProjectDir()
        {
            string binDir = Path.Combine(_tempDir, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "TestLib.dll"), "fake");

            var taskEnv = new TrackingTaskEnvironment { ProjectDirectory = _tempDir };
            taskEnv.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

            var task = new Fixed.AssemblyReferenceResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                References = new ITaskItem[] { new TaskItem("TestLib") },
                TargetFramework = "net8.0",
                RuntimeIdentifier = "win-x64"
            };

            task.Execute();

            // Fixed task uses TaskEnvironment.GetAbsolutePath for runtime pack resolution
            Assert.NotEmpty(task.ResolvedReferences);
            bool runtimePackResolvedViaTaskEnv = taskEnv.GetAbsolutePathArgs.Any(arg =>
                arg.Contains("packs") && arg.Contains("Microsoft.NETCore.App.Runtime"));
            Assert.True(runtimePackResolvedViaTaskEnv,
                "Fixed task should use TaskEnvironment.GetAbsolutePath() for runtime pack path resolution");
        }

        #endregion
    }
}
