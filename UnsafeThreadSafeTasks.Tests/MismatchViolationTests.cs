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

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            var task = new BrokenMismatch.NullChecksTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // The broken task uses Path.GetFullPath even when TaskEnvironment is provided,
            // resolving relative to CWD instead of ProjectDirectory
            Assert.True(result);
            // Verify the file was actually found at the correct location
            Assert.DoesNotContain(_engine.Messages, m => m.Message!.Contains("does not exist"));
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

            // Assert CORRECT behavior: task succeeds and finds the file
            Assert.True(result);
            Assert.DoesNotContain(_engine.Messages, m => m.Message!.Contains("does not exist"));
        }

        #endregion

        #region IgnoresTaskEnvironment

        [Fact]
        public void IgnoresTaskEnvironment_Broken_ShouldResolveToProjectDir()
        {
            var fileName = "ignore-env-test.txt";
            File.WriteAllText(Path.Combine(_projectDir, fileName), "content");

            var taskEnv = TaskEnvironmentHelper.CreateForTest(_projectDir);
            taskEnv.SetEnvironmentVariable("TEST_CONFIG", "test-value");
            var task = new BrokenMismatch.IgnoresTaskEnvironment
            {
                BuildEngine = _engine,
                InputPath = fileName,
                EnvVarName = "TEST_CONFIG",
                TaskEnvironment = taskEnv
            };

            bool result = task.Execute();

            // The broken task uses Path.GetFullPath (CWD-based) and Environment.GetEnvironmentVariable (global),
            // so the file resolved to CWD/ignore-env-test.txt instead of _projectDir/ignore-env-test.txt
            Assert.True(result);
            // Verify the file was actually found (not reported as missing)
            Assert.DoesNotContain(_engine.Messages, m => m.Message!.Contains("does not exist"));
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

            // Assert CORRECT behavior: task should succeed
            Assert.True(result);
        }

        #endregion
    }
}
