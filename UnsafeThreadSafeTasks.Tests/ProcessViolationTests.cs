using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;
using Broken = UnsafeThreadSafeTasks.ProcessViolations;
using Fixed = FixedThreadSafeTasks.ProcessViolations;

#nullable disable

namespace UnsafeThreadSafeTasks.Tests
{
    public class ProcessViolationTests
    {
        // ── UsesRawProcessStartInfo ─────────────────────────────────────

        [Fact]
        public void UsesRawProcessStartInfo_BrokenTask_ShouldSetWorkingDirectoryToProjectDir()
        {
            var engine = new MockBuildEngine();
            var projectDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var task = new Broken.UsesRawProcessStartInfo
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                    Command = "cmd.exe",
                    Arguments = "/c cd"
                };

                bool result = task.Execute();

                // Assert CORRECT behavior: task should run in ProjectDirectory
                Assert.True(result);
                Assert.Contains(engine.Messages!, m => m.Message!.Contains(projectDir));
            }
            finally
            {
                TestHelper.CleanupTempDirectory(projectDir);
            }
        }

        [Fact]
        public void UsesRawProcessStartInfo_FixedTask_ShouldSetWorkingDirectoryToProjectDir()
        {
            var engine = new MockBuildEngine();
            var projectDir = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                var task = new Fixed.UsesRawProcessStartInfo
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                    Command = "cmd.exe",
                    Arguments = "/c cd"
                };

                bool result = task.Execute();

                // Assert CORRECT behavior: task should run in ProjectDirectory
                Assert.True(result);
                Assert.Contains(engine.Messages!, m => m.Message!.Contains(projectDir));
            }
            finally
            {
                TestHelper.CleanupTempDirectory(projectDir);
            }
        }

        // ── CallsEnvironmentExit ────────────────────────────────────────
        // The broken task calls Environment.Exit() which would crash the test host.
        // We assert correct structure: it should implement IMultiThreadableTask with TaskEnvironment.

        [Fact]
        public void CallsEnvironmentExit_BrokenTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Broken.CallsEnvironmentExit();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsEnvironmentExit_FixedTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Fixed.CallsEnvironmentExit();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsEnvironmentExit_FixedTask_ShouldReturnFalseAndLogError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsEnvironmentExit
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                ExitCode = 1
            };

            bool result = task.Execute();

            Assert.False(result);
            Assert.Contains(engine.Errors!, e => e.Message!.Contains("exit code 1"));
        }

        // ── CallsEnvironmentFailFast ────────────────────────────────────

        [Fact]
        public void CallsEnvironmentFailFast_BrokenTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Broken.CallsEnvironmentFailFast();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsEnvironmentFailFast_FixedTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Fixed.CallsEnvironmentFailFast();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsEnvironmentFailFast_FixedTask_ShouldReturnFalseAndLogError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsEnvironmentFailFast
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                ErrorMessage = "Something went wrong"
            };

            bool result = task.Execute();

            Assert.False(result);
            Assert.Contains(engine.Errors!, e => e.Message!.Contains("Something went wrong"));
        }

        // ── CallsProcessKill ────────────────────────────────────────────

        [Fact]
        public void CallsProcessKill_BrokenTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Broken.CallsProcessKill();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsProcessKill_FixedTask_ShouldImplementIMultiThreadableTask()
        {
            var task = new Fixed.CallsProcessKill();

            // Assert CORRECT behavior: task should implement IMultiThreadableTask
            Assert.IsAssignableFrom<IMultiThreadableTask>(task);
        }

        [Fact]
        public void CallsProcessKill_FixedTask_ShouldReturnFalseAndLogError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsProcessKill
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest()
            };

            bool result = task.Execute();

            Assert.False(result);
            Assert.Single(engine.Errors);
        }
    }
}
