using Xunit;
using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Broken = UnsafeThreadSafeTasks.EnvironmentViolations;
using Fixed = FixedThreadSafeTasks.EnvironmentViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class EnvironmentViolationTests : IDisposable
    {
        private readonly List<string> _tempDirs = new();
        private readonly List<string> _envVarsToClean = new();

        private string CreateTempDir()
        {
            var dir = TestHelper.CreateNonCwdTempDirectory();
            _tempDirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            foreach (var dir in _tempDirs)
                TestHelper.CleanupTempDirectory(dir);
            foreach (var name in _envVarsToClean)
                Environment.SetEnvironmentVariable(name, null);
        }

        private string SetGlobalEnvVar(string value)
        {
            var name = "MSBUILD_TEST_" + Guid.NewGuid().ToString("N")[..8];
            Environment.SetEnvironmentVariable(name, value);
            _envVarsToClean.Add(name);
            return name;
        }

        // =====================================================================
        // UsesEnvironmentGetVariable
        // =====================================================================

        [Fact]
        public void UsesEnvironmentGetVariable_BrokenTask_ShouldReadFromTaskEnvironment()
        {
            var varName = SetGlobalEnvVar("GLOBAL_VALUE");
            var taskEnv = TaskEnvironmentHelper.CreateForTest();
            taskEnv.SetEnvironmentVariable(varName, "TASK_VALUE");

            var task = new Broken.UsesEnvironmentGetVariable
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                VariableName = varName
            };

            task.Execute();

            // Assert CORRECT behavior: task should read from TaskEnvironment, not global env
            Assert.Equal("TASK_VALUE", task.VariableValue);
        }

        [Fact]
        public void UsesEnvironmentGetVariable_FixedTask_ShouldReadFromTaskEnvironment()
        {
            var varName = SetGlobalEnvVar("GLOBAL_VALUE");
            var taskEnv = TaskEnvironmentHelper.CreateForTest();
            taskEnv.SetEnvironmentVariable(varName, "TASK_VALUE");

            var task = new Fixed.UsesEnvironmentGetVariable
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                VariableName = varName
            };

            task.Execute();

            // Assert CORRECT behavior: task should read from TaskEnvironment, not global env
            Assert.Equal("TASK_VALUE", task.VariableValue);
        }

        // =====================================================================
        // UsesEnvironmentSetVariable
        // =====================================================================

        [Fact]
        public void UsesEnvironmentSetVariable_BrokenTask_ShouldNotModifyGlobalState()
        {
            var varName = "MSBUILD_SET_TEST_" + Guid.NewGuid().ToString("N")[..8];
            _envVarsToClean.Add(varName);

            var taskEnv = TaskEnvironmentHelper.CreateForTest();

            var task = new Broken.UsesEnvironmentSetVariable
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                VariableName = varName,
                VariableValue = "BROKEN_SET"
            };

            task.Execute();

            // Assert CORRECT behavior: global env should NOT be modified
            Assert.Null(Environment.GetEnvironmentVariable(varName));
            // The value should be stored in TaskEnvironment
            Assert.Equal("BROKEN_SET", taskEnv.GetEnvironmentVariable(varName));
        }

        [Fact]
        public void UsesEnvironmentSetVariable_FixedTask_ShouldNotModifyGlobalState()
        {
            var varName = "MSBUILD_SET_TEST_" + Guid.NewGuid().ToString("N")[..8];
            _envVarsToClean.Add(varName);

            var taskEnv = TaskEnvironmentHelper.CreateForTest();

            var task = new Fixed.UsesEnvironmentSetVariable
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                VariableName = varName,
                VariableValue = "FIXED_SET"
            };

            task.Execute();

            // Assert CORRECT behavior: global env should NOT be modified
            Assert.Null(Environment.GetEnvironmentVariable(varName));
            // The value should be stored in TaskEnvironment
            Assert.Equal("FIXED_SET", taskEnv.GetEnvironmentVariable(varName));
        }

        // =====================================================================
        // ReadsEnvironmentCurrentDirectory
        // =====================================================================

        [Fact]
        public void ReadsEnvironmentCurrentDirectory_BrokenTask_ShouldReadProjectDirectory()
        {
            var projectDir = CreateTempDir();

            var task = new Broken.ReadsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)
            };

            task.Execute();

            // Assert CORRECT behavior: task should return ProjectDirectory, not process CWD
            Assert.Equal(projectDir, task.CurrentDir);
        }

        [Fact]
        public void ReadsEnvironmentCurrentDirectory_FixedTask_ShouldReadProjectDirectory()
        {
            var projectDir = CreateTempDir();

            var task = new Fixed.ReadsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)
            };

            task.Execute();

            // Assert CORRECT behavior: task should return ProjectDirectory, not process CWD
            Assert.Equal(projectDir, task.CurrentDir);
        }

        // =====================================================================
        // SetsEnvironmentCurrentDirectory
        // =====================================================================

        [Fact]
        public void SetsEnvironmentCurrentDirectory_BrokenTask_ShouldNotModifyGlobalCwd()
        {
            var projectDir = CreateTempDir();
            var originalCwd = Environment.CurrentDirectory;

            var task = new Broken.SetsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                NewDirectory = projectDir
            };

            task.Execute();

            // Assert CORRECT behavior: Environment.CurrentDirectory should be unchanged
            Assert.Equal(originalCwd, Environment.CurrentDirectory);

            // Restore CWD in case broken task changed it
            Environment.CurrentDirectory = originalCwd;
        }

        [Fact]
        public void SetsEnvironmentCurrentDirectory_FixedTask_ShouldNotModifyGlobalCwd()
        {
            var projectDir = CreateTempDir();
            var originalCwd = Environment.CurrentDirectory;

            var task = new Fixed.SetsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                NewDirectory = projectDir
            };

            task.Execute();

            // Assert CORRECT behavior: Environment.CurrentDirectory should be unchanged
            Assert.Equal(originalCwd, Environment.CurrentDirectory);
        }
    }
}
