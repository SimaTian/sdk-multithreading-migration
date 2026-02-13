using System.Linq;
using Xunit;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Microsoft.Build.Framework;
using BrokenSubtle = UnsafeThreadSafeTasks.SubtleViolations;
using FixedSubtle = FixedThreadSafeTasks.SubtleViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class SubtleViolationTests : IDisposable
    {
        private readonly string _projectDir;
        private readonly MockBuildEngine _engine;

        public SubtleViolationTests()
        {
            _projectDir = TestHelper.CreateNonCwdTempDirectory();
            _engine = new MockBuildEngine();
        }

        public void Dispose()
        {
            TestHelper.CleanupTempDirectory(_projectDir);
        }

        #region IndirectPathGetFullPath

        [Fact]
        public void IndirectPathGetFullPath_Broken_ShouldResolveToProjectDir()
        {
            var fileName = "indirect-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "hello");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.IndirectPathGetFullPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: resolved path should be relative to projectDir
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, fileName), task.ResolvedPath);
        }

        [Fact]
        public void IndirectPathGetFullPath_Fixed_ShouldResolveToProjectDir()
        {
            var fileName = "indirect-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "hello");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.IndirectPathGetFullPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: resolved path should be relative to projectDir
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, fileName), task.ResolvedPath);
        }

        #endregion

        #region LambdaCapturesCurrentDirectory

        [Fact]
        public void LambdaCapturesCurrentDirectory_Broken_ShouldResolveToProjectDir()
        {
            var relativePaths = new[] { "file1.txt", "subdir\\file2.txt" };

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.LambdaCapturesCurrentDirectory
            {
                BuildEngine = _engine,
                RelativePaths = relativePaths,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: paths should resolve relative to projectDir
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, "file1.txt"), task.AbsolutePaths[0]);
            Assert.Equal(Path.Combine(_projectDir, "subdir\\file2.txt"), task.AbsolutePaths[1]);
        }

        [Fact]
        public void LambdaCapturesCurrentDirectory_Fixed_ShouldResolveToProjectDir()
        {
            var relativePaths = new[] { "file1.txt", "subdir\\file2.txt" };

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.LambdaCapturesCurrentDirectory
            {
                BuildEngine = _engine,
                RelativePaths = relativePaths,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: paths should resolve relative to projectDir
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, "file1.txt"), task.AbsolutePaths[0]);
            Assert.Equal(Path.Combine(_projectDir, "subdir\\file2.txt"), task.AbsolutePaths[1]);
        }

        #endregion

        #region SharedMutableStaticField

        [Fact]
        public void SharedMutableStaticField_Broken_ShouldHaveInstanceIsolation()
        {
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);

            var task1 = new BrokenSubtle.SharedMutableStaticField
            {
                BuildEngine = _engine,
                InputFile = "fileA.txt",
                TaskEnvironment = taskEnv
            };

            var task2 = new BrokenSubtle.SharedMutableStaticField
            {
                BuildEngine = _engine,
                InputFile = "fileB.txt",
                TaskEnvironment = taskEnv
            };

            task1.Execute();
            task2.Execute();

            // Assert CORRECT behavior: each instance should have its own execution count
            Assert.Equal(1, task1.ExecutionNumber);
            Assert.Equal(1, task2.ExecutionNumber);
        }

        [Fact]
        public void SharedMutableStaticField_Fixed_ShouldHaveInstanceIsolation()
        {
            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);

            var task1 = new FixedSubtle.SharedMutableStaticField
            {
                BuildEngine = _engine,
                InputFile = "fileA.txt",
                TaskEnvironment = taskEnv
            };

            var task2 = new FixedSubtle.SharedMutableStaticField
            {
                BuildEngine = _engine,
                InputFile = "fileB.txt",
                TaskEnvironment = taskEnv
            };

            task1.Execute();
            task2.Execute();

            // Assert CORRECT behavior: each instance should have its own execution count
            Assert.Equal(1, task1.ExecutionNumber);
            Assert.Equal(1, task2.ExecutionNumber);
        }

        #endregion

        #region PartialMigration

        [Fact]
        public void PartialMigration_Broken_ShouldResolveBothToProjectDir()
        {
            var primaryFile = "primary.txt";
            var secondaryFile = "secondary.txt";
            File.WriteAllText(Path.Combine(_projectDir, primaryFile), "same");
            File.WriteAllText(Path.Combine(_projectDir, secondaryFile), "same");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.PartialMigration
            {
                BuildEngine = _engine,
                PrimaryPath = primaryFile,
                SecondaryPath = secondaryFile,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: both paths resolve to ProjectDir, files found and match
            Assert.True(result);
            Assert.True(task.FilesMatch);
        }

        [Fact]
        public void PartialMigration_Fixed_ShouldResolveBothToProjectDir()
        {
            var primaryFile = "primary.txt";
            var secondaryFile = "secondary.txt";
            File.WriteAllText(Path.Combine(_projectDir, primaryFile), "same");
            File.WriteAllText(Path.Combine(_projectDir, secondaryFile), "same");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.PartialMigration
            {
                BuildEngine = _engine,
                PrimaryPath = primaryFile,
                SecondaryPath = secondaryFile,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: both paths resolve to ProjectDir, files found and match
            Assert.True(result);
            Assert.True(task.FilesMatch);
        }

        #endregion

        #region DoubleResolvesPath

        [Fact]
        public void DoubleResolvesPath_Broken_ShouldUseTaskEnvironmentOnly()
        {
            var fileName = "double-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.DoubleResolvesPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            Assert.True(result);
            Assert.StartsWith(_projectDir, task.CanonicalPath);
        }

        [Fact]
        public void DoubleResolvesPath_Fixed_ShouldUseTaskEnvironmentOnly()
        {
            var fileName = "double-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.DoubleResolvesPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            Assert.True(result);
            Assert.StartsWith(_projectDir, task.CanonicalPath);
        }

        #endregion

        #region SourceEncodingFixer

        [Fact]
        public void SourceEncodingFixer_Broken_ShouldFixEncoding()
        {
            string srcDir = Path.Combine(_projectDir, "src");
            Directory.CreateDirectory(srcDir);
            // Write a file without BOM (plain ASCII/UTF-8 no BOM)
            File.WriteAllBytes(Path.Combine(srcDir, "Test.cs"),
                System.Text.Encoding.ASCII.GetBytes("// test file content"));

            var task = new BrokenSubtle.SourceEncodingFixer
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_projectDir),
                SourceDirectory = srcDir,
                FileExtensions = ".cs",
                TargetEncoding = "utf-8-bom",
                CreateBackups = false,
                NormalizeLineEndings = false
            };

            bool result;
            try { result = task.Execute(); } catch { result = false; }

            Assert.True(result);
            Assert.NotEmpty(task.ModifiedFiles);
        }

        [Fact]
        public void SourceEncodingFixer_Fixed_ShouldFixEncoding()
        {
            string srcDir = Path.Combine(_projectDir, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllBytes(Path.Combine(srcDir, "Test.cs"),
                System.Text.Encoding.ASCII.GetBytes("// test file content"));

            var task = new FixedSubtle.SourceEncodingFixer
            {
                BuildEngine = _engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(_projectDir),
                SourceDirectory = srcDir,
                FileExtensions = ".cs",
                TargetEncoding = "utf-8-bom",
                CreateBackups = false,
                NormalizeLineEndings = false
            };

            bool result = task.Execute();

            Assert.True(result, $"Execute failed. Errors: {string.Join("; ", _engine.Errors.Select(e => e.Message))}");
            Assert.NotEmpty(task.ModifiedFiles);
            // Verify the file was actually re-encoded with BOM
            byte[] bytes = File.ReadAllBytes(Path.Combine(srcDir, "Test.cs"));
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "File should have UTF-8 BOM after encoding fix");
        }

        [Fact]
        public void SourceEncodingFixer_FixedTask_ShouldHaveModifiedFilesOutput()
        {
            var task = new FixedSubtle.SourceEncodingFixer();
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
            Assert.NotNull(task.ModifiedFiles);
        }

        #endregion
    }
}
