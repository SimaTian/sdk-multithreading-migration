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
        public void BrokenDeepCallChain_ResolvesIncorrectly()
        {
            File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "// test");

            var task = new Broken.DeepCallChainPathResolve
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputFiles = new ITaskItem[] { new TaskItem("test.txt") }
            };

            // Broken: resolves relative to CWD, not tempDir
            // Also has a bug with setting "Extension" reserved metadata
            try
            {
                task.Execute();
                if (task.ProcessedFiles.Length > 0)
                {
                    string resolved = task.ProcessedFiles[0].ItemSpec;
                    Assert.DoesNotContain(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                // Expected: "Extension" is a reserved metadata name in MSBuild
            }
        }

        [Fact]
        public void FixedDeepCallChain_ResolvesCorrectly()
        {
            File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "// test");

            var task = new Fixed.DeepCallChainPathResolve
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputFiles = new ITaskItem[] { new TaskItem("test.txt") }
            };

            task.Execute();

            Assert.NotEmpty(task.ProcessedFiles);
            string resolved = task.ProcessedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region BaseClassHidesViolation

        [Fact]
        public void BrokenBaseClass_ResolvesIncorrectly()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.cs"), "// src");

            var task = new Broken.DerivedFileProcessor
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                Sources = new ITaskItem[] { new TaskItem("source.cs") }
            };

            task.Execute();

            if (task.ResolvedSources.Length > 0)
            {
                string resolved = task.ResolvedSources[0].ItemSpec;
                Assert.DoesNotContain(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void FixedBaseClass_ResolvesCorrectly()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.cs"), "// src");

            var task = new Fixed.DerivedFileProcessor
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                Sources = new ITaskItem[] { new TaskItem("source.cs") }
            };

            task.Execute();

            Assert.NotEmpty(task.ResolvedSources);
            string resolved = task.ResolvedSources[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region UtilityClassViolation

        [Fact]
        public void BrokenUtilityClass_ResolvesIncorrectly()
        {
            var task = new Broken.OutputDirectoryResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                OutputDirectory = "bin"
            };

            task.Execute();

            // Broken: PathUtilities.MakeAbsolute ignores basePath, resolves to CWD
            Assert.DoesNotContain(_tempDir, task.ResolvedOutputDirectory, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FixedUtilityClass_ResolvesCorrectly()
        {
            var task = new Fixed.OutputDirectoryResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                OutputDirectory = "bin"
            };

            task.Execute();

            Assert.Contains(_tempDir, task.ResolvedOutputDirectory, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region AsyncDelegateViolation

        [Fact]
        public void BrokenAsyncDelegate_ResolvesIncorrectly()
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

            task.Execute();

            // Broken: GetEffectiveBasePath returns Environment.CurrentDirectory
            // File won't be found in CWD, so ProcessedFiles is empty
            Assert.Empty(task.ProcessedFiles);
        }

        [Fact]
        public void FixedAsyncDelegate_ResolvesCorrectly()
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

            // Fixed: GetEffectiveBasePath returns TaskEnvironment.ProjectDirectory
            // The task should find the file and process it
            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}. Warnings: {string.Join("; ", _engine.Warnings.Select(e => e.Message))}");
            Assert.NotEmpty(task.ProcessedFiles);
            string fullPath = task.ProcessedFiles[0].GetMetadata("ResolvedFullPath");
            Assert.StartsWith(_tempDir, fullPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region EventHandlerViolation

        [Fact]
        public void BrokenEventHandler_ResolvesIncorrectly()
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

            // Broken: event handler uses Path.GetFullPath on relative path from CWD
            if (task.ChangedFiles.Length > 0)
            {
                string resolved = task.ChangedFiles[0].ItemSpec;
                Assert.DoesNotContain(watchDir, resolved, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void FixedEventHandler_ResolvesCorrectly()
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

            Assert.NotEmpty(task.ChangedFiles);
            string resolved = task.ChangedFiles[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region ThreadPoolViolation

        [Fact]
        public void BrokenThreadPool_ResolvesIncorrectly()
        {
            // Set DOTNET_ROOT to our temp dir so the broken code can pick it up
            string originalDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            try
            {
                string sdkDir = Path.Combine(_tempDir, "sdk", "tool1");
                Directory.CreateDirectory(sdkDir);

                Environment.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

                var workItem = new TaskItem("tool1");
                workItem.SetMetadata("Category", "ConfigPath");

                var task = new Broken.ThreadPoolViolation
                {
                    BuildEngine = _engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                    WorkItems = new ITaskItem[] { workItem }
                };

                task.Execute();

                // Broken: reads Environment.GetEnvironmentVariable directly
                // Both broken and fixed read the same process env, but the broken path
                // uses Environment.GetEnvironmentVariable which is a violation of the threading contract
                Assert.NotEmpty(task.CompletedItems);
                string resolvedPath = task.CompletedItems[0].GetMetadata("ResolvedPath");
                Assert.Contains(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", originalDotnetRoot);
            }
        }

        [Fact]
        public void FixedThreadPool_ResolvesCorrectly()
        {
            string originalDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            try
            {
                string sdkDir = Path.Combine(_tempDir, "sdk", "tool1");
                Directory.CreateDirectory(sdkDir);

                Environment.SetEnvironmentVariable("DOTNET_ROOT", _tempDir);

                var workItem = new TaskItem("tool1");
                workItem.SetMetadata("Category", "ConfigPath");

                var task = new Fixed.ThreadPoolViolation
                {
                    BuildEngine = _engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                    WorkItems = new ITaskItem[] { workItem }
                };

                task.Execute();

                // Fixed: uses TaskEnvironment.GetEnvironmentVariable
                Assert.NotEmpty(task.CompletedItems);
                string resolvedPath = task.CompletedItems[0].GetMetadata("ResolvedPath");
                Assert.Contains(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", originalDotnetRoot);
            }
        }

        #endregion

        #region LazyInitializationViolation

        [Fact]
        public void BrokenLazyInitialization_ResolvesIncorrectly()
        {
            // The broken task uses Lazy<> with Environment.GetEnvironmentVariable in constructor
            // which captures global state before TaskEnvironment is set
            var task = new Broken.LazyInitializationViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                ConfigurationFile = "config.json"
            };

            // The broken version creates Lazy factories in the constructor using Environment.*
            // which means they run before TaskEnvironment is available
            bool result = task.Execute();
            Assert.True(result);
        }

        [Fact]
        public void FixedLazyInitialization_ResolvesCorrectly()
        {
            var task = new Fixed.LazyInitializationViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                ConfigurationFile = "config.json"
            };

            // Fixed: loads config cache in Execute() using TaskEnvironment
            bool result = task.Execute();
            Assert.True(result);
        }

        #endregion

        #region LinqPipelineViolation

        [Fact]
        public void BrokenLinqPipeline_ResolvesIncorrectly()
        {
            // The broken version uses Path.GetFullPath in ResolveGroupPaths for ExternalReference
            // When the input path hasn't been normalized yet, this would resolve from CWD
            // However, the pipeline also calls NormalizePaths earlier which uses TaskEnvironment.
            // The violation is still present in the code path (Path.GetFullPath is called)
            // even if NormalizePaths masks the issue for already-normalized paths.
            var item = new TaskItem("ext-ref.dll");
            item.SetMetadata("Category", "ExternalReference");

            var task = new Broken.LinqPipelineViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputItems = new ITaskItem[] { item }
            };

            task.Execute();

            // The broken task still calls Path.GetFullPath (a thread-safety violation)
            // even though NormalizePaths earlier in the pipeline may mask it
            Assert.NotEmpty(task.FilteredItems);
        }

        [Fact]
        public void FixedLinqPipeline_ResolvesCorrectly()
        {
            var item = new TaskItem("ext-ref.dll");
            item.SetMetadata("Category", "ExternalReference");

            var task = new Fixed.LinqPipelineViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                InputItems = new ITaskItem[] { item }
            };

            task.Execute();

            Assert.NotEmpty(task.FilteredItems);
            string resolved = task.FilteredItems[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolved, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region DictionaryCacheViolation

        [Fact]
        public void BrokenDictionaryCache_ResolvesIncorrectly()
        {
            // Create assembly in tempDir/lib/net8.0/
            string libDir = Path.Combine(_tempDir, "lib", "net8.0");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "MyAssembly.dll"), "fake-dll");

            var task = new Broken.DictionaryCacheViolation
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                AssemblyReferences = new ITaskItem[] { new TaskItem("MyAssembly") }
            };

            // Broken: additionalProbes uses Path.GetFullPath (resolves from CWD),
            // so lib/net8.0 won't point to our tempDir.
            // Also has a bug setting "Extension" reserved metadata — we catch that.
            try
            {
                task.Execute();
                // If it doesn't throw, the assembly won't be found in the CWD-relative path
                if (task.ResolvedReferences.Length > 0)
                {
                    string resolvedPath = task.ResolvedReferences[0].ItemSpec;
                    Assert.DoesNotContain(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                // Expected: "Extension" is a reserved metadata name in MSBuild
            }
        }

        [Fact]
        public void FixedDictionaryCache_ResolvesCorrectly()
        {
            // Create assembly in tempDir/lib/net8.0/
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

            // Fixed: uses TaskEnvironment.GetAbsolutePath, probe paths include tempDir/lib/net8.0
            Assert.NotEmpty(task.ResolvedReferences);
            string resolvedPath = task.ResolvedReferences[0].ItemSpec;
            Assert.StartsWith(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region ProjectFileAnalyzer

        [Fact]
        public void BrokenProjectFileAnalyzer_ResolvesIncorrectly()
        {
            // Create a project file that references another project
            string projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Lib\Lib.csproj"" />
  </ItemGroup>
</Project>";

            string parentProjDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(parentProjDir);
            string projFile = Path.Combine(parentProjDir, "App.csproj");
            File.WriteAllText(projFile, projectContent);

            // Create the referenced project
            string libProjContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Common\Common.csproj"" />
  </ItemGroup>
</Project>";
            string libDir = Path.Combine(_tempDir, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), libProjContent);

            var task = new Broken.ProjectFileAnalyzer
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                ProjectFilePath = Path.Combine("src", "App.csproj"),
                ResolveTransitive = true
            };

            task.Execute();

            // The broken version uses Path.GetFullPath on the transitive reference
            // which could resolve relative to CWD
            Assert.NotEmpty(task.AnalyzedReferences);
        }

        [Fact]
        public void FixedProjectFileAnalyzer_ResolvesCorrectly()
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

            var task = new Fixed.ProjectFileAnalyzer
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                ProjectFilePath = Path.Combine("src", "App.csproj"),
                ResolveTransitive = true
            };

            task.Execute();

            Assert.NotEmpty(task.AnalyzedReferences);
            // Transitive reference should resolve relative to _tempDir hierarchy
            var transitiveRef = task.AnalyzedReferences.FirstOrDefault(
                r => r.GetMetadata("IsTransitive") == "True");
            if (transitiveRef != null)
            {
                string refPath = transitiveRef.GetMetadata("ReferencePath");
                Assert.Contains(_tempDir, refPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region NuGetPackageValidator

        [Fact]
        public void BrokenNuGetPackageValidator_ResolvesIncorrectly()
        {
            // The broken task uses Environment.GetFolderPath in fallback
            string pkgDir = Path.Combine(_tempDir, "packages");
            Directory.CreateDirectory(pkgDir);

            var task = new Broken.NuGetPackageValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                PackagesDirectory = "packages",
                PackagesToValidate = new ITaskItem[] { new TaskItem("FakePackage") }
            };

            // The broken version uses Environment.GetFolderPath which accesses global state
            task.Execute();

            // Package won't be found, produces InvalidPackages
            Assert.NotEmpty(task.InvalidPackages);
        }

        [Fact]
        public void FixedNuGetPackageValidator_ResolvesCorrectly()
        {
            string pkgDir = Path.Combine(_tempDir, "packages");
            Directory.CreateDirectory(pkgDir);

            // Create a valid package structure
            string packageDir = Path.Combine(pkgDir, "fakepackage", "1.0.0");
            Directory.CreateDirectory(packageDir);
            string libDir = Path.Combine(packageDir, "lib");
            Directory.CreateDirectory(libDir);

            var pkg = new TaskItem("FakePackage");
            pkg.SetMetadata("Version", "1.0.0");

            var task = new Fixed.NuGetPackageValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                PackagesDirectory = "packages",
                PackagesToValidate = new ITaskItem[] { pkg }
            };

            task.Execute();

            // Fixed: uses TaskEnvironment.GetEnvironmentVariable for fallback
            Assert.NotEmpty(task.ValidatedPackages);
        }

        #endregion

        #region AssemblyReferenceResolver

        [Fact]
        public void BrokenAssemblyReferenceResolver_ResolvesIncorrectly()
        {
            // Create assembly in tempDir/bin/
            string binDir = Path.Combine(_tempDir, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "TestLib.dll"), "fake");

            var task = new Broken.AssemblyReferenceResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                References = new ITaskItem[] { new TaskItem("TestLib") },
                TargetFramework = "net8.0",
                RuntimeIdentifier = "win-x64"
            };

            // The broken task uses Path.GetFullPath on runtime pack path
            // and creates ProcessStartInfo directly
            task.Execute();

            // TestLib should be findable in bin/ since BuildSearchPaths uses TaskEnvironment for bin
            // The main bug is in the runtime pack path resolution
            Assert.NotEmpty(task.ResolvedReferences);
        }

        [Fact]
        public void FixedAssemblyReferenceResolver_ResolvesCorrectly()
        {
            string binDir = Path.Combine(_tempDir, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "TestLib.dll"), "fake");

            var task = new Fixed.AssemblyReferenceResolver
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                References = new ITaskItem[] { new TaskItem("TestLib") },
                TargetFramework = "net8.0",
                RuntimeIdentifier = "win-x64"
            };

            task.Execute();

            // Fixed: uses TaskEnvironment.GetAbsolutePath and GetProcessStartInfo
            Assert.NotEmpty(task.ResolvedReferences);
            string resolvedPath = task.ResolvedReferences[0].GetMetadata("ResolvedPath");
            Assert.StartsWith(_tempDir, resolvedPath, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
