using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;

using BrokenComplex = UnsafeThreadSafeTasks.ComplexViolations;
using FixedComplex = FixedThreadSafeTasks.ComplexViolations;
using BrokenMismatch = UnsafeThreadSafeTasks.MismatchViolations;
using FixedMismatch = FixedThreadSafeTasks.MismatchViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    /// <summary>
    /// Verifies Fixed tasks produce correct output content from a non-CWD ProjectDirectory.
    /// Goes beyond "file exists" — checks actual content, counts, and correctness.
    /// </summary>
    public class OutputCorrectnessTests
    {
        // ── ArtifactPublisher ────────────────────────────────────────────

        [Fact]
        public void ArtifactPublisher_Fixed_ManifestContainsCorrectPaths()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var srcDir = Path.Combine(tempDir, "bin", "Release");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "App.dll"), "dll-data");
                File.WriteAllText(Path.Combine(srcDir, "App.xml"), "xml-data");

                var publishDir = Path.Combine(tempDir, "publish");
                var manifestPath = Path.Combine(tempDir, "manifest.json");

                var engine = new MockBuildEngine();
                var task = new FixedComplex.ArtifactPublisher
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectories = new ITaskItem[] { new TaskItem(Path.Combine("bin", "Release")) },
                    PublishDirectory = "publish",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll;.xml"
                };

                bool result = task.Execute();

                Assert.True(result, string.Join("; ", engine.Errors.Select(e => e.Message)));
                Assert.Equal(2, task.TotalFilesCopied);

                // Verify actual file content was copied correctly
                Assert.Equal("dll-data", File.ReadAllText(Path.Combine(publishDir, "App.dll")));
                Assert.Equal("xml-data", File.ReadAllText(Path.Combine(publishDir, "App.xml")));

                // Verify manifest exists and has content
                Assert.True(File.Exists(manifestPath));
                var manifest = File.ReadAllText(manifestPath);
                Assert.Contains("App.dll", manifest);
                Assert.Contains("App.xml", manifest);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void ArtifactPublisher_Broken_ProducesWrongOutputFromNonCwdDir()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var srcDir = Path.Combine(tempDir, "bin", "Release");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "App.dll"), "dll-data");

                var engine = new MockBuildEngine();
                var task = new BrokenComplex.ArtifactPublisher
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SourceDirectories = new ITaskItem[] { new TaskItem(Path.Combine("bin", "Release")) },
                    PublishDirectory = "publish",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll"
                };

                bool result = task.Execute();

                // Should resolve publish dir under ProjectDirectory
                Assert.True(result, "Task should succeed");
                var correctLocation = Path.Combine(tempDir, "publish", "App.dll");
                Assert.True(File.Exists(correctLocation),
                    "Published files should be under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── BuildLogCollector ────────────────────────────────────────────

        [Fact]
        public void BuildLogCollector_Fixed_CorrectlyCountsErrorsAndWarnings()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Create realistic log files
                var logDir = Path.Combine(tempDir, "logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "build1.log"),
                    "Build started\nfoo.cs(1,1): error CS0001: Something broke\nfoo.cs(2,1): warning CS0168: Variable unused\nBuild complete");
                File.WriteAllText(Path.Combine(logDir, "build2.log"),
                    "Build started\nbar.cs(1,1): error CS0002: Another error\nBuild complete");

                var reportPath = Path.Combine(tempDir, "report.html");

                var engine = new MockBuildEngine();
                var task = new FixedComplex.BuildLogCollector
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    LogDirectories = new ITaskItem[] { new TaskItem("logs") },
                    OutputReportPath = "report.html",
                    IncludeWarnings = true
                };

                bool result = task.Execute();

                Assert.True(result, string.Join("; ", engine.Errors.Select(e => e.Message)));
                Assert.Equal(2, task.TotalErrors);
                Assert.Equal(1, task.TotalWarnings);

                // Verify report is written to correct location with content
                Assert.True(File.Exists(reportPath), "Report should be under ProjectDirectory");
                var reportContent = File.ReadAllText(reportPath);
                Assert.Contains("CS0001", reportContent);
                Assert.Contains("CS0002", reportContent);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void BuildLogCollector_Broken_CannotFindLogsFromNonCwdDir()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var logDir = Path.Combine(tempDir, "logs");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "build1.log"),
                    "foo.cs(1,1): error CS0001: Something broke");

                var engine = new MockBuildEngine();
                var task = new BrokenComplex.BuildLogCollector
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    LogDirectories = new ITaskItem[] { new TaskItem("logs") },
                    OutputReportPath = "report.html",
                    IncludeWarnings = true
                };

                bool result = task.Execute();

                // Should write report under ProjectDirectory and find 1 error
                Assert.True(result, "Task should succeed");
                var reportInTempDir = Path.Combine(tempDir, "report.html");
                Assert.True(File.Exists(reportInTempDir),
                    "Report should be under ProjectDirectory");
                Assert.Equal(1, task.TotalErrors);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── AssemblyVersionPatcher ───────────────────────────────────────

        [Fact]
        public void AssemblyVersionPatcher_Fixed_ActuallyPatchesVersionInFile()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var csprojContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Version>1.0.0</Version>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
  </PropertyGroup>
</Project>";
                File.WriteAllText(Path.Combine(tempDir, "MyLib.csproj"), csprojContent);

                var engine = new MockBuildEngine();
                var task = new FixedMismatch.AssemblyVersionPatcher
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    ProjectFiles = new ITaskItem[] { new TaskItem("MyLib.csproj") },
                    VersionPrefix = "3.1.4",
                    VersionSuffix = "rc.1",
                    CreateBackups = false
                };

                bool result = task.Execute();

                Assert.True(result, string.Join("; ", engine.Errors.Select(e => e.Message)));

                // Verify the actual file content was changed
                var patched = File.ReadAllText(Path.Combine(tempDir, "MyLib.csproj"));
                Assert.Contains("3.1.4", patched);
                Assert.DoesNotContain("<Version>1.0.0</Version>", patched);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void AssemblyVersionPatcher_Broken_FailsToPatchFromNonCwdDir()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var csprojContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>";
                File.WriteAllText(Path.Combine(tempDir, "MyLib.csproj"), csprojContent);

                var engine = new MockBuildEngine();
                var task = new BrokenMismatch.AssemblyVersionPatcher
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    ProjectFiles = new ITaskItem[] { new TaskItem("MyLib.csproj") },
                    VersionPrefix = "3.1.4",
                    CreateBackups = false
                };

                bool result = task.Execute();

                // Should find and patch the file under ProjectDirectory
                Assert.True(result, "Task should succeed");
                var content = File.ReadAllText(Path.Combine(tempDir, "MyLib.csproj"));
                Assert.Contains("3.1.4", content);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        // ── SolutionBackupManager ────────────────────────────────────────

        [Fact]
        public void SolutionBackupManager_Fixed_CreatesBackupInCorrectLocation()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Create a minimal solution structure
                File.WriteAllText(Path.Combine(tempDir, "MySolution.sln"), "Microsoft Visual Studio Solution File");
                File.WriteAllText(Path.Combine(tempDir, "Project.csproj"), "<Project />");

                var backupDir = Path.Combine(tempDir, "backups");

                var engine = new MockBuildEngine();
                var task = new FixedComplex.SolutionBackupManager
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SolutionPath = "MySolution.sln",
                    BackupRootDirectory = "backups",
                    Mode = "backup",
                    FilePatterns = "*.csproj;*.sln"
                };

                bool result = task.Execute();

                Assert.True(result, string.Join("; ", engine.Errors.Select(e => e.Message)));
                Assert.False(string.IsNullOrEmpty(task.BackupId));

                // Verify backup dir was created under tempDir
                Assert.True(Directory.Exists(backupDir),
                    "Backup dir should be under ProjectDirectory");
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public void SolutionBackupManager_Broken_BackupGoesToWrongLocation()
        {
            var tempDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "MySolution.sln"), "solution content");

                var engine = new MockBuildEngine();
                var task = new BrokenComplex.SolutionBackupManager
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(tempDir),
                    SolutionPath = "MySolution.sln",
                    // No BackupRootDirectory → broken code uses Environment.GetFolderPath (global)
                    Mode = "backup",
                    FilePatterns = "*.sln"
                };

                bool result = task.Execute();

                // Should create backup under a predictable location related to ProjectDirectory
                Assert.True(result, "Task should succeed");
                // Verify backup was created under ProjectDirectory, not in AppData
                var hasBackupUnderTempDir = Directory.GetDirectories(tempDir)
                    .Any(d => Path.GetFileName(d).StartsWith("backup", StringComparison.OrdinalIgnoreCase)
                           || Path.GetFileName(d).Contains("SolutionBackup")
                           || Path.GetFileName(d).Contains("bak"));
                Assert.True(hasBackupUnderTempDir,
                    "Backup should be created under ProjectDirectory, not global AppData");
            }
            finally { Directory.Delete(tempDir, true); }
        }
    }
}
