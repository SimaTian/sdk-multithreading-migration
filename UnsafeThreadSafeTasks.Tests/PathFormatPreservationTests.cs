using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;

using BrokenComplex = UnsafeThreadSafeTasks.ComplexViolations;
using FixedComplex = FixedThreadSafeTasks.ComplexViolations;
using BrokenSubtle = UnsafeThreadSafeTasks.SubtleViolations;
using FixedSubtle = FixedThreadSafeTasks.SubtleViolations;
using BrokenMismatch = UnsafeThreadSafeTasks.MismatchViolations;
using FixedMismatch = FixedThreadSafeTasks.MismatchViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    /// <summary>
    /// Verifies that Fixed tasks resolve output paths against ProjectDirectory (not CWD).
    /// Broken tasks call Path.GetFullPath which resolves against CWD — these tests
    /// reliably FAIL for Broken and PASS for Fixed when run from a non-CWD directory.
    /// </summary>
    public class PathFormatPreservationTests
    {
        // ── ArtifactPublisher ────────────────────────────────────────────

        [Fact]
        public void ArtifactPublisher_Fixed_OutputPathsResolveToProjectDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Setup: source dir with files
                var srcDir = Path.Combine(tempDir, "bin", "Release");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "App.dll"), "dll-content");
                File.WriteAllText(Path.Combine(srcDir, "App.pdb"), "pdb-content");

                var publishDir = Path.Combine(tempDir, "publish");
                var manifestPath = Path.Combine(tempDir, "manifest.json");

                var task = new FixedComplex.ArtifactPublisher
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectories = new ITaskItem[] { new TaskItem(Path.Combine("bin", "Release")) },
                    PublishDirectory = "publish",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll;.pdb"
                };

                task.Execute();

                // Fixed task resolves relative paths against ProjectDirectory (tempDir)
                Assert.True(File.Exists(Path.Combine(publishDir, "App.dll")),
                    "Published file should be under ProjectDirectory/publish");
                Assert.True(File.Exists(manifestPath),
                    "Manifest should be under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void ArtifactPublisher_Broken_OutputPathsResolveToWrongDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Setup: create source files in tempDir
                var srcDir = Path.Combine(tempDir, "bin", "Release");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "App.dll"), "dll-content");

                var task = new BrokenComplex.ArtifactPublisher
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectories = new ITaskItem[] { new TaskItem(Path.Combine("bin", "Release")) },
                    PublishDirectory = "publish",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll"
                };

                task.Execute();

                // Should resolve "publish" under ProjectDirectory
                var expectedLocation = Path.Combine(tempDir, "publish", "App.dll");
                Assert.True(File.Exists(expectedLocation),
                    "Task should resolve publish dir under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── SourceEncodingFixer ──────────────────────────────────────────

        [Fact]
        public void SourceEncodingFixer_Fixed_ModifiedFilesReferenceProjectDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Create a .cs file without BOM
                var srcSubDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(srcSubDir);
                File.WriteAllBytes(Path.Combine(srcSubDir, "Test.cs"),
                    System.Text.Encoding.UTF8.GetBytes("// no BOM file"));

                var task = new FixedSubtle.SourceEncodingFixer
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectory = "src",
                    FileExtensions = ".cs",
                    TargetEncoding = "utf-8-bom",
                    NormalizeLineEndings = true,
                    CreateBackups = false
                };

                task.Execute();

                // Fixed: ModifiedFiles paths should reference tempDir
                Assert.NotNull(task.ModifiedFiles);
                if (task.ModifiedFiles.Length > 0)
                {
                    foreach (var item in task.ModifiedFiles)
                    {
                        Assert.StartsWith(tempDir, item.ItemSpec, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void SourceEncodingFixer_Broken_ModifiedFilesResolveToWrongDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var srcSubDir = Path.Combine(tempDir, "src");
                Directory.CreateDirectory(srcSubDir);
                File.WriteAllBytes(Path.Combine(srcSubDir, "Test.cs"),
                    System.Text.Encoding.UTF8.GetBytes("// no BOM file"));

                var task = new BrokenSubtle.SourceEncodingFixer
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectory = "src",
                    FileExtensions = ".cs",
                    TargetEncoding = "utf-8-bom",
                    NormalizeLineEndings = true,
                    CreateBackups = false
                };

                task.Execute();

                // Should resolve "src" under ProjectDirectory
                bool anyInTempDir = task.ModifiedFiles != null &&
                    task.ModifiedFiles.Any(m => m.ItemSpec.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase));
                Assert.True(anyInTempDir,
                    "ModifiedFiles should resolve under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── ProjectDependencyGraphBuilder ────────────────────────────────

        [Fact]
        public void ProjectDependencyGraphBuilder_Fixed_GraphOutputUnderProjectDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Create minimal .csproj files
                var projDir = Path.Combine(tempDir, "projects");
                Directory.CreateDirectory(projDir);
                File.WriteAllText(Path.Combine(projDir, "LibA.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");

                var graphPath = Path.Combine(tempDir, "graph.dot");

                var task = new FixedComplex.ProjectDependencyGraphBuilder
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SolutionDirectory = "projects",
                    GraphOutputPath = "graph.dot"
                };

                task.Execute();

                // Fixed: graph file should be written under tempDir
                Assert.True(File.Exists(graphPath),
                    "Graph output should be created under ProjectDirectory");
                Assert.NotNull(task.BuildOrder);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void ProjectDependencyGraphBuilder_Broken_GraphOutputResolvesToWrongLocation()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var projDir = Path.Combine(tempDir, "projects");
                Directory.CreateDirectory(projDir);
                File.WriteAllText(Path.Combine(projDir, "LibA.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");

                var task = new BrokenComplex.ProjectDependencyGraphBuilder
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SolutionDirectory = "projects",
                    GraphOutputPath = "graph.dot"
                };

                task.Execute();

                // Should write graph to ProjectDirectory
                var graphInTempDir = Path.Combine(tempDir, "graph.dot");
                Assert.True(File.Exists(graphInTempDir),
                    "Task should write graph under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── AssemblyVersionPatcher ───────────────────────────────────────

        [Fact]
        public void AssemblyVersionPatcher_Fixed_PatchedFilesReferenceProjectDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Create a minimal .csproj with version
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "MyLib.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><Version>1.0.0</Version></PropertyGroup></Project>");

                var task = new FixedMismatch.AssemblyVersionPatcher
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    ProjectFiles = new ITaskItem[] { new TaskItem("MyLib.csproj") },
                    VersionPrefix = "2.0.0",
                    CreateBackups = false
                };

                task.Execute();

                // Fixed: PatchedFiles should reference tempDir
                Assert.NotNull(task.PatchedFiles);
                if (task.PatchedFiles.Length > 0)
                {
                    foreach (var item in task.PatchedFiles)
                    {
                        Assert.StartsWith(tempDir, item.ItemSpec, StringComparison.OrdinalIgnoreCase);
                    }
                }

                // Verify the file was actually patched
                var content = File.ReadAllText(Path.Combine(tempDir, "MyLib.csproj"));
                Assert.Contains("2.0.0", content);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void AssemblyVersionPatcher_Broken_PatchedFilesResolveToWrongDirectory()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "MyLib.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><Version>1.0.0</Version></PropertyGroup></Project>");

                var task = new BrokenMismatch.AssemblyVersionPatcher
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    ProjectFiles = new ITaskItem[] { new TaskItem("MyLib.csproj") },
                    VersionPrefix = "2.0.0",
                    CreateBackups = false
                };

                task.Execute();

                // Should resolve PatchedFiles under ProjectDirectory
                bool anyInTempDir = task.PatchedFiles != null &&
                    task.PatchedFiles.Any(p => p.ItemSpec.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase));
                Assert.True(anyInTempDir,
                    "PatchedFiles should resolve under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }
    }
}
