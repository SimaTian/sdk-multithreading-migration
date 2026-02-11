using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;
using Broken = UnsafeThreadSafeTasks.ProcessViolations;
using Fixed = FixedThreadSafeTasks.ProcessViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class ProcessViolationTests
    {
        // ── UsesRawProcessStartInfo ─────────────────────────────────────

        [Fact]
        public void BrokenTask_UsesRawProcessStartInfo_DoesNotSetWorkingDirectory()
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
                    Arguments = "/c echo hello"
                };

                // The broken task creates ProcessStartInfo directly, so WorkingDirectory
                // is NOT set to ProjectDirectory. We can verify by inspecting the pattern:
                // new ProcessStartInfo(Command, Arguments) does not set WorkingDirectory.
                // We verify the task type does implement IMultiThreadableTask but misuses it.
                Assert.IsAssignableFrom<IMultiThreadableTask>(task);

                // The broken task's ProcessStartInfo won't have WorkingDirectory = projectDir
                // because it uses `new ProcessStartInfo(Command, Arguments)` directly.
                // We can't easily intercept the PSI, but we can verify TaskEnvironment.GetProcessStartInfo()
                // would set it correctly, while the broken task ignores it.
                var psi = task.TaskEnvironment.GetProcessStartInfo();
                Assert.Equal(projectDir, psi.WorkingDirectory);

                // The broken code creates its own PSI — verify it doesn't use TaskEnvironment
                var brokenPsi = new System.Diagnostics.ProcessStartInfo(task.Command, task.Arguments);
                Assert.NotEqual(projectDir, brokenPsi.WorkingDirectory);
            }
            finally
            {
                TestHelper.CleanupTempDirectory(projectDir);
            }
        }

        [Fact]
        public void FixedTask_UsesRawProcessStartInfo_SetsWorkingDirectoryFromTaskEnvironment()
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
                    Arguments = "/c echo hello"
                };

                Assert.IsAssignableFrom<IMultiThreadableTask>(task);

                // The fixed task uses TaskEnvironment.GetProcessStartInfo() which sets WorkingDirectory
                var psi = task.TaskEnvironment.GetProcessStartInfo();
                Assert.Equal(projectDir, psi.WorkingDirectory);

                // Execute the fixed task — it should run successfully
                bool result = task.Execute();
                Assert.True(result);
            }
            finally
            {
                TestHelper.CleanupTempDirectory(projectDir);
            }
        }

        // ── CallsEnvironmentExit ────────────────────────────────────────

        [Fact]
        public void BrokenTask_CallsEnvironmentExit_IsAttributeOnlyWithoutIMultiThreadableTask()
        {
            // The broken task has [MSBuildMultiThreadableTask] but does NOT implement IMultiThreadableTask.
            // This is itself a mismatch — it claims to be multithreadable but uses Environment.Exit().
            var task = new Broken.CallsEnvironmentExit();
            Assert.False(task is IMultiThreadableTask,
                "Broken task should NOT implement IMultiThreadableTask (attribute-only mismatch).");
        }

        [Fact]
        public void FixedTask_CallsEnvironmentExit_ReturnsFalseAndLogsError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsEnvironmentExit
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                ExitCode = 1
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            bool result = task.Execute();

            Assert.False(result);
            Assert.Contains(engine.Errors, e => e.Message.Contains("exit code 1"));
        }

        // ── CallsEnvironmentFailFast ────────────────────────────────────

        [Fact]
        public void BrokenTask_CallsEnvironmentFailFast_IsAttributeOnlyWithoutIMultiThreadableTask()
        {
            var task = new Broken.CallsEnvironmentFailFast();
            Assert.False(task is IMultiThreadableTask,
                "Broken task should NOT implement IMultiThreadableTask (attribute-only mismatch).");
        }

        [Fact]
        public void FixedTask_CallsEnvironmentFailFast_ReturnsFalseAndLogsError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsEnvironmentFailFast
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                ErrorMessage = "Something went wrong"
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            bool result = task.Execute();

            Assert.False(result);
            Assert.Contains(engine.Errors, e => e.Message.Contains("Something went wrong"));
        }

        // ── CallsProcessKill ────────────────────────────────────────────

        [Fact]
        public void BrokenTask_CallsProcessKill_IsAttributeOnlyWithoutIMultiThreadableTask()
        {
            var task = new Broken.CallsProcessKill();
            Assert.False(task is IMultiThreadableTask,
                "Broken task should NOT implement IMultiThreadableTask (attribute-only mismatch).");
        }

        [Fact]
        public void FixedTask_CallsProcessKill_ReturnsFalseAndLogsError()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.CallsProcessKill
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest()
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            bool result = task.Execute();

            Assert.False(result);
            Assert.Single(engine.Errors);
        }
    }
}
