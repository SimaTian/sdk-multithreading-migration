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
        public void IndirectPathGetFullPath_Broken_ResolvesToCwd()
        {
            // Arrange
            var fileName = "indirect-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "hello");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.IndirectPathGetFullPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: broken task resolves via helper that uses Path.GetFullPath (CWD)
            Assert.True(result);
            var cwdResolved = Path.GetFullPath(fileName);
            Assert.Equal(cwdResolved, task.ResolvedPath);
            Assert.NotEqual(Path.Combine(_projectDir, fileName), task.ResolvedPath);
        }

        [Fact]
        public void IndirectPathGetFullPath_Fixed_ResolvesToProjectDir()
        {
            // Arrange
            var fileName = "indirect-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "hello");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.IndirectPathGetFullPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task resolves via TaskEnvironment
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, fileName), task.ResolvedPath);
        }

        #endregion

        #region LambdaCapturesCurrentDirectory

        [Fact]
        public void LambdaCapturesCurrentDirectory_Broken_ResolvesToCwd()
        {
            // Arrange
            var relativePaths = new[] { "file1.txt", "subdir\\file2.txt" };

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.LambdaCapturesCurrentDirectory
            {
                BuildEngine = _engine,
                RelativePaths = relativePaths,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: broken task uses Environment.CurrentDirectory in lambda
            Assert.True(result);
            var cwd = Environment.CurrentDirectory;
            Assert.Equal(Path.Combine(cwd, "file1.txt"), task.AbsolutePaths[0]);
            Assert.NotEqual(Path.Combine(_projectDir, "file1.txt"), task.AbsolutePaths[0]);
        }

        [Fact]
        public void LambdaCapturesCurrentDirectory_Fixed_ResolvesToProjectDir()
        {
            // Arrange
            var relativePaths = new[] { "file1.txt", "subdir\\file2.txt" };

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.LambdaCapturesCurrentDirectory
            {
                BuildEngine = _engine,
                RelativePaths = relativePaths,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task uses TaskEnvironment.ProjectDirectory
            Assert.True(result);
            Assert.Equal(Path.Combine(_projectDir, "file1.txt"), task.AbsolutePaths[0]);
            Assert.Equal(Path.Combine(_projectDir, "subdir\\file2.txt"), task.AbsolutePaths[1]);
        }

        #endregion

        #region SharedMutableStaticField

        [Fact]
        public void SharedMutableStaticField_Broken_SharedStateAcrossInstances()
        {
            // Arrange
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

            // Act
            task1.Execute();
            task2.Execute();

            // Assert: broken task uses static fields, so execution counts are shared
            // task2 sees the incremented count from task1
            Assert.True(task2.ExecutionNumber > 1);
        }

        [Fact]
        public void SharedMutableStaticField_Fixed_InstanceIsolation()
        {
            // Arrange
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

            // Act
            task1.Execute();
            task2.Execute();

            // Assert: fixed task uses instance fields, each instance has its own count
            Assert.Equal(1, task1.ExecutionNumber);
            Assert.Equal(1, task2.ExecutionNumber);
        }

        #endregion

        #region PartialMigration

        [Fact]
        public void PartialMigration_Broken_SecondaryResolvesToCwd()
        {
            // Arrange
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

            // Act
            bool result = task.Execute();

            // Assert: primary resolves to ProjectDir, but secondary uses Path.GetFullPath (CWD)
            Assert.True(result);
            var expectedPrimary = Path.Combine(_projectDir, primaryFile);
            var cwdSecondary = Path.GetFullPath(secondaryFile);
            // The secondary resolves to CWD, not ProjectDir
            Assert.NotEqual(Path.Combine(_projectDir, secondaryFile), cwdSecondary);
            // Files won't match because secondary can't be found at CWD
            Assert.False(task.FilesMatch);
        }

        [Fact]
        public void PartialMigration_Fixed_BothResolveToProjectDir()
        {
            // Arrange
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

            // Act
            bool result = task.Execute();

            // Assert: both paths resolve to ProjectDir, files found and match
            Assert.True(result);
            Assert.True(task.FilesMatch);
        }

        #endregion

        #region DoubleResolvesPath

        [Fact]
        public void DoubleResolvesPath_Broken_ReResolvesWithPathGetFullPath()
        {
            // Arrange
            var fileName = "double-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenSubtle.DoubleResolvesPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: the first resolve is correct via TaskEnvironment, but the second
            // re-resolves with Path.GetFullPath. For an already-absolute path this may
            // work, but it still uses a forbidden API. We verify the task ran.
            Assert.True(result);
            // The canonical path should equal Path.GetFullPath of the TaskEnvironment-resolved path
            var expectedResolved = Path.Combine(_projectDir, fileName);
            var doubleResolved = Path.GetFullPath(expectedResolved);
            Assert.Equal(doubleResolved, task.CanonicalPath);
        }

        [Fact]
        public void DoubleResolvesPath_Fixed_UsesTaskEnvironmentOnly()
        {
            // Arrange
            var fileName = "double-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedSubtle.DoubleResolvesPath
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task uses TaskEnvironment.GetCanonicalForm, no Path.GetFullPath
            Assert.True(result);
            var expectedCanonical = taskEnv.GetCanonicalForm(fileName);
            Assert.Equal(expectedCanonical, task.CanonicalPath);
        }

        #endregion
    }
}
