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
        public void AttributeOnlyWithForbiddenApis_Broken_ShouldResolveToProjectDir()
        {
            var fileName = "test-input.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "content");

            var task = new BrokenMismatch.AttributeOnlyWithForbiddenApis
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_VAR"
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: task should find the file at ProjectDir
            Assert.True(result);
            Assert.DoesNotContain(_engine.Messages, m => m.Message!.Contains("File not found"));
        }

        [Fact]
        public void AttributeOnlyWithForbiddenApis_Fixed_ShouldResolveToProjectDir()
        {
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

            bool result = task.Execute();

            // Assert CORRECT behavior: task should find the file at ProjectDir
            Assert.True(result);
            Assert.DoesNotContain(_engine.Messages, m => m.Message!.Contains("File not found"));
        }

        #endregion

        #region NullChecksTaskEnvironment

        [Fact]
        public void NullChecksTaskEnvironment_Broken_ShouldResolveToProjectDir()
        {
            var fileName = "null-check-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "data");

            // Set TaskEnvironment to null to trigger the broken task's fallback to Path.GetFullPath
            var task = new BrokenMismatch.NullChecksTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = null!
            };

            bool result = task.Execute();

            // The broken task null-checks TaskEnvironment and falls back to Path.GetFullPath,
            // which resolves relative to CWD instead of ProjectDirectory
            Assert.True(result);
            var expectedResolved = Path.Combine(_projectDir, fileName);
            Assert.Contains(_engine.Messages, m => m.Message!.Contains(expectedResolved));
        }

        [Fact]
        public void NullChecksTaskEnvironment_Fixed_ShouldResolveToProjectDir()
        {
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

            bool result = task.Execute();

            // Assert CORRECT behavior: resolved path should contain projectDir
            Assert.True(result);
            var expectedResolved = Path.Combine(_projectDir, fileName);
            Assert.Contains(_engine.Messages, m => m.Message!.Contains(expectedResolved));
        }

        #endregion

        #region IgnoresTaskEnvironment

        [Fact]
        public void IgnoresTaskEnvironment_Broken_ShouldResolveToProjectDir()
        {
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

            bool result = task.Execute();

            // Assert CORRECT behavior: task should find the file via TaskEnvironment
            Assert.True(result);
            Assert.Contains(_engine.Messages, m => m.Message!.Contains("Processing file"));
        }

        [Fact]
        public void IgnoresTaskEnvironment_Fixed_ShouldResolveToProjectDir()
        {
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

            bool result = task.Execute();

            // Assert CORRECT behavior: task should find the file via TaskEnvironment
            Assert.True(result);
            Assert.Contains(_engine.Messages, m => m.Message!.Contains("Processing file"));
        }

        #endregion
    }
}
