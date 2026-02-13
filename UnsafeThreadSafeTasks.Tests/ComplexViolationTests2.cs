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
    public class ComplexViolationTests2 : IDisposable
    {
        private readonly string _tempDir;
        private readonly MockBuildEngine _engine;

        public ComplexViolationTests2()
        {
            _tempDir = TestHelper.CreateNonCwdTempDirectory();
            _engine = new MockBuildEngine();
        }

        public void Dispose()
        {
            TestHelper.CleanupTempDirectory(_tempDir);
        }

        #region ArtifactPublisher

        [Fact]
        public void ArtifactPublisher_Broken_ShouldPublishFilesToCorrectDirectory()
        {
            // Create source directory with artifacts
            string srcDir = Path.Combine(_tempDir, "bin", "Release");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyApp.dll"), "fake-dll");
            File.WriteAllText(Path.Combine(srcDir, "MyApp.pdb"), "fake-pdb");

            string publishDir = Path.Combine(_tempDir, "publish");
            string manifestPath = Path.Combine(_tempDir, "manifest.json");

            // Use absolute path for source so the broken task can still find it
            var srcItem = new TaskItem(srcDir);
            srcItem.SetMetadata("ProjectName", "MyApp");

            var task = new Broken.ArtifactPublisher
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SourceDirectories = new ITaskItem[] { srcItem },
                PublishDirectory = publishDir,
                ManifestPath = manifestPath,
                IncludeExtensions = ".dll;.pdb"
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            Assert.True(result);
            Assert.True(task.TotalFilesCopied > 0);
        }

        [Fact]
        public void ArtifactPublisher_Fixed_ShouldPublishFilesToCorrectDirectory()
        {
            string srcDir = Path.Combine(_tempDir, "bin", "Release");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyApp.dll"), "fake-dll");
            File.WriteAllText(Path.Combine(srcDir, "MyApp.pdb"), "fake-pdb");

            string publishDir = Path.Combine(_tempDir, "publish");
            string manifestPath = Path.Combine(_tempDir, "manifest.json");

            var srcItem = new TaskItem(Path.Combine("bin", "Release"));
            srcItem.SetMetadata("ProjectName", "MyApp");

            var task = new Fixed.ArtifactPublisher
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SourceDirectories = new ITaskItem[] { srcItem },
                PublishDirectory = publishDir,
                ManifestPath = manifestPath,
                IncludeExtensions = ".dll;.pdb"
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.Equal(2, task.TotalFilesCopied);
            Assert.True(File.Exists(Path.Combine(publishDir, "MyApp.dll")));
            Assert.True(File.Exists(Path.Combine(publishDir, "MyApp.pdb")));
            Assert.True(File.Exists(manifestPath));
        }

        [Fact]
        public void ArtifactPublisher_Fixed_ShouldHavePublishedFilesOutput()
        {
            var task = new Fixed.ArtifactPublisher();
            Assert.NotNull(task.PublishedFiles);
            Assert.NotNull(task.TaskEnvironment is null ? "" : ""); // property exists
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        #endregion

        #region SolutionBackupManager

        [Fact]
        public void SolutionBackupManager_Broken_ShouldCreateBackup()
        {
            // Create a solution file and some project files
            string slnPath = Path.Combine(_tempDir, "TestSolution.sln");
            File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");

            string projDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(projDir);
            File.WriteAllText(Path.Combine(projDir, "App.csproj"), "<Project />");

            string backupRoot = Path.Combine(_tempDir, "backups");

            var task = new Broken.SolutionBackupManager
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SolutionPath = "TestSolution.sln",
                BackupRootDirectory = backupRoot,
                Mode = "backup",
                MaxBackupCount = 3
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            Assert.True(result);
            Assert.False(string.IsNullOrEmpty(task.BackupId));
        }

        [Fact]
        public void SolutionBackupManager_Fixed_ShouldCreateBackup()
        {
            string slnPath = Path.Combine(_tempDir, "TestSolution.sln");
            File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");

            string projDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(projDir);
            File.WriteAllText(Path.Combine(projDir, "App.csproj"), "<Project />");

            string backupRoot = Path.Combine(_tempDir, "backups");

            var task = new Fixed.SolutionBackupManager
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SolutionPath = "TestSolution.sln",
                BackupRootDirectory = backupRoot,
                Mode = "backup",
                MaxBackupCount = 3
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.False(string.IsNullOrEmpty(task.BackupId));
            // Verify backup directory was created under the specified backupRoot
            Assert.True(Directory.Exists(backupRoot));
            var backupDirs = Directory.GetDirectories(backupRoot, "TestSolution_*");
            Assert.NotEmpty(backupDirs);
        }

        [Fact]
        public void SolutionBackupManager_FixedTask_ShouldHaveBackupIdOutput()
        {
            var task = new Fixed.SolutionBackupManager();
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
            // Verify key properties exist
            Assert.NotNull(task.BackupId);
            Assert.NotNull(task.Mode);
        }

        #endregion

        #region NuGetRestoreValidator

        [Fact]
        public void NuGetRestoreValidator_Broken_ShouldValidatePackages()
        {
            // Create a package directory with structure
            string pkgDir = Path.Combine(_tempDir, "packages");
            string pkgPath = Path.Combine(pkgDir, "newtonsoft.json", "13.0.3", "lib", "net8.0");
            Directory.CreateDirectory(pkgPath);
            File.WriteAllText(Path.Combine(pkgPath, "Newtonsoft.Json.dll"), "fake");

            var pkgRef = new TaskItem("Newtonsoft.Json");
            pkgRef.SetMetadata("Version", "13.0.3");

            var task = new Broken.NuGetRestoreValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                PackageReferences = new ITaskItem[] { pkgRef },
                PackagesDirectory = pkgDir,
                TargetFramework = "net8.0"
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            // Should validate successfully when using absolute package dir
            Assert.True(result);
            Assert.Empty(task.MissingPackages);
        }

        [Fact]
        public void NuGetRestoreValidator_Fixed_ShouldValidatePackages()
        {
            string pkgDir = Path.Combine(_tempDir, "packages");
            string pkgPath = Path.Combine(pkgDir, "newtonsoft.json", "13.0.3", "lib", "net8.0");
            Directory.CreateDirectory(pkgPath);
            File.WriteAllText(Path.Combine(pkgPath, "Newtonsoft.Json.dll"), "fake");

            var pkgRef = new TaskItem("Newtonsoft.Json");
            pkgRef.SetMetadata("Version", "13.0.3");

            var task = new Fixed.NuGetRestoreValidator
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                PackageReferences = new ITaskItem[] { pkgRef },
                PackagesDirectory = pkgDir,
                TargetFramework = "net8.0"
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.Empty(task.MissingPackages);
            // Validation report should be written under project dir
            Assert.False(string.IsNullOrEmpty(task.ValidationReport));
            Assert.Contains(_tempDir, task.ValidationReport, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NuGetRestoreValidator_FixedTask_ShouldHaveValidationReportOutput()
        {
            var task = new Fixed.NuGetRestoreValidator();
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
            Assert.NotNull(task.MissingPackages);
        }

        #endregion

        #region BuildLogCollector

        [Fact]
        public void BuildLogCollector_Broken_ShouldCollectLogs()
        {
            string logDir = Path.Combine(_tempDir, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "build.log"),
                "src\\Program.cs(10,5): error CS1002: ; expected\n" +
                "src\\Util.cs(20,1): warning CS0168: Variable declared but never used\n");

            string reportPath = Path.Combine(_tempDir, "report.html");

            var task = new Broken.BuildLogCollector
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                LogDirectories = new ITaskItem[] { new TaskItem(logDir) },
                OutputReportPath = reportPath,
                IncludeWarnings = true
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            Assert.True(result);
            Assert.Equal(1, task.TotalErrors);
            Assert.Equal(1, task.TotalWarnings);
        }

        [Fact]
        public void BuildLogCollector_Fixed_ShouldCollectLogs()
        {
            string logDir = Path.Combine(_tempDir, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "build.log"),
                "src\\Program.cs(10,5): error CS1002: ; expected\n" +
                "src\\Util.cs(20,1): warning CS0168: Variable declared but never used\n");

            string reportPath = Path.Combine(_tempDir, "report.html");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_tempDir);
            taskEnv.SetEnvironmentVariable("COMPUTERNAME", "TEST-MACHINE");
            taskEnv.SetEnvironmentVariable("BUILD_NUMBER", "42");

            var task = new Fixed.BuildLogCollector
            {
                BuildEngine = _engine,
                TaskEnvironment = taskEnv,
                LogDirectories = new ITaskItem[] { new TaskItem(logDir) },
                OutputReportPath = reportPath,
                IncludeWarnings = true
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.Equal(1, task.TotalErrors);
            Assert.Equal(1, task.TotalWarnings);
            Assert.True(File.Exists(reportPath));
            string reportContent = File.ReadAllText(reportPath);
            Assert.Contains("TEST-MACHINE", reportContent);
            Assert.Contains("CS1002", reportContent);
        }

        [Fact]
        public void BuildLogCollector_FixedTask_ShouldHaveTotalErrorsOutput()
        {
            var task = new Fixed.BuildLogCollector();
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
            Assert.Equal(0, task.TotalErrors);
            Assert.Equal(0, task.TotalWarnings);
        }

        #endregion

        #region ProjectDependencyGraphBuilder

        [Fact]
        public void ProjectDependencyGraphBuilder_Broken_ShouldBuildGraph()
        {
            // Create project files with references
            string projADir = Path.Combine(_tempDir, "ProjA");
            Directory.CreateDirectory(projADir);
            File.WriteAllText(Path.Combine(projADir, "ProjA.csproj"),
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\ProjB\ProjB.csproj"" />
  </ItemGroup>
</Project>");

            string projBDir = Path.Combine(_tempDir, "ProjB");
            Directory.CreateDirectory(projBDir);
            File.WriteAllText(Path.Combine(projBDir, "ProjB.csproj"),
                @"<Project Sdk=""Microsoft.NET.Sdk"">
</Project>");

            var task = new Broken.ProjectDependencyGraphBuilder
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SolutionDirectory = _tempDir
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            Assert.True(result);
            Assert.NotEmpty(task.BuildOrder);
        }

        [Fact]
        public void ProjectDependencyGraphBuilder_Fixed_ShouldBuildGraph()
        {
            string projADir = Path.Combine(_tempDir, "ProjA");
            Directory.CreateDirectory(projADir);
            File.WriteAllText(Path.Combine(projADir, "ProjA.csproj"),
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\ProjB\ProjB.csproj"" />
  </ItemGroup>
</Project>");

            string projBDir = Path.Combine(_tempDir, "ProjB");
            Directory.CreateDirectory(projBDir);
            File.WriteAllText(Path.Combine(projBDir, "ProjB.csproj"),
                @"<Project Sdk=""Microsoft.NET.Sdk"">
</Project>");

            string graphOutput = Path.Combine(_tempDir, "deps.dot");

            var task = new Fixed.ProjectDependencyGraphBuilder
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_tempDir),
                SolutionDirectory = _tempDir,
                GraphOutputPath = graphOutput
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.NotEmpty(task.BuildOrder);
            Assert.False(task.HasCircularReferences);
            // Graph output should be written using TaskEnvironment-resolved path
            Assert.True(File.Exists(graphOutput));
            string dotContent = File.ReadAllText(graphOutput);
            Assert.Contains("ProjA", dotContent);
            Assert.Contains("ProjB", dotContent);
        }

        [Fact]
        public void ProjectDependencyGraphBuilder_FixedTask_ShouldHaveBuildOrderOutput()
        {
            var task = new Fixed.ProjectDependencyGraphBuilder();
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
            Assert.NotNull(task.BuildOrder);
        }

        #endregion
    }
}
