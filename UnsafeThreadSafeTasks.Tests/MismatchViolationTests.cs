using Xunit;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Microsoft.Build.Framework;
using BrokenMismatch = UnsafeThreadSafeTasks.MismatchViolations;
using FixedMismatch = FixedThreadSafeTasks.MismatchViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class MismatchViolationTests : IDisposable
    {
        private readonly string _projectDir;
        private readonly MockBuildEngine _engine;

        public MismatchViolationTests()
        {
            _projectDir = TestHelper.CreateNonCwdTempDirectory();
            _engine = new MockBuildEngine();
        }

        public void Dispose()
        {
            TestHelper.CleanupTempDirectory(_projectDir);
        }

        #region AttributeOnlyWithForbiddenApis

        [Fact]
        public void AttributeOnlyWithForbiddenApis_Broken_ResolvesToCwd()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "test-input.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "content");

            var task = new BrokenMismatch.AttributeOnlyWithForbiddenApis
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_VAR"
            };

            // Act
            bool result = task.Execute();

            // Assert: broken task uses Path.GetFullPath which resolves relative to CWD, not ProjectDir
            Assert.True(result);
            // The broken task resolves to CWD, so it won't find the file in ProjectDir
            var resolvedByCwd = Path.GetFullPath(fileName);
            var resolvedByProjectDir = Path.Combine(_projectDir, fileName);
            Assert.NotEqual(resolvedByProjectDir, resolvedByCwd);
            // Verify the broken task logged "File not found" since it resolved to CWD
            Assert.Contains(_engine.Messages, m => m.Message.Contains("File not found"));
        }

        [Fact]
        public void AttributeOnlyWithForbiddenApis_Fixed_ResolvesToProjectDir()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "test-input.txt";
            var filePath = Path.Combine(_projectDir, fileName);
            File.WriteAllText(filePath, "content");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedMismatch.AttributeOnlyWithForbiddenApis
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_VAR",
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task uses TaskEnvironment, resolves to ProjectDir and finds the file
            Assert.True(result);
            Assert.DoesNotContain(_engine.Messages, m => m.Message.Contains("File not found"));
        }

        #endregion

        #region NullChecksTaskEnvironment

        [Fact]
        public void NullChecksTaskEnvironment_Broken_FallsBackToPathGetFullPath()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "null-check-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            // When TaskEnvironment is set to null, the broken task falls back to Path.GetFullPath
            var task = new BrokenMismatch.NullChecksTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = null!
            };

            // Act
            bool result = task.Execute();

            // Assert: falls back to CWD-based resolution
            Assert.True(result);
            var cwdResolved = Path.GetFullPath(fileName);
            Assert.Contains(_engine.Messages, m => m.Message.Contains(cwdResolved));
        }

        [Fact]
        public void NullChecksTaskEnvironment_Fixed_AlwaysUsesTaskEnvironment()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "null-check-test.txt";
            var filePath = Path.Combine(_projectDir, fileName);
            File.WriteAllText(filePath, "data");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedMismatch.NullChecksTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task always uses TaskEnvironment, resolves to ProjectDir
            Assert.True(result);
            var expectedResolved = Path.Combine(_projectDir, fileName);
            Assert.Contains(_engine.Messages, m => m.Message.Contains(expectedResolved));
        }

        #endregion

        #region IgnoresTaskEnvironment

        [Fact]
        public void IgnoresTaskEnvironment_Broken_ResolvesToCwd()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "ignore-env-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "content");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenMismatch.IgnoresTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_CONFIG",
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: broken task ignores TaskEnvironment and uses Path.GetFullPath (CWD)
            Assert.True(result);
            var cwdResolved = Path.GetFullPath(fileName);
            // Since CWD != ProjectDir, it won't find the file
            Assert.Contains(_engine.Messages, m => m.Message.Contains("does not exist"));
        }

        [Fact]
        public void IgnoresTaskEnvironment_Fixed_ResolvesToProjectDir()
        {
            // Arrange: create a file in ProjectDir
            var fileName = "ignore-env-test.txt";
            var filePath = Path.Combine(_projectDir, fileName);
            File.WriteAllText(filePath, "content");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new FixedMismatch.IgnoresTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_CONFIG",
                TaskEnvironment = taskEnv
            };

            // Act
            bool result = task.Execute();

            // Assert: fixed task uses TaskEnvironment, finds the file
            Assert.True(result);
            Assert.Contains(_engine.Messages, m => m.Message.Contains("Processing file"));
        }

        #endregion
    }
}
