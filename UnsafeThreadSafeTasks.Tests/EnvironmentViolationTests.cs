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
        public void UsesEnvironmentGetVariable_BrokenTask_ResolvesRelativeToCwd()
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

            // Broken task uses Environment.GetEnvironmentVariable directly — gets GLOBAL_VALUE
            Assert.Equal("GLOBAL_VALUE", task.VariableValue);
        }

        [Fact]
        public void UsesEnvironmentGetVariable_FixedTask_ResolvesRelativeToProjectDirectory()
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

            // Fixed task uses TaskEnvironment.GetEnvironmentVariable — gets TASK_VALUE
            Assert.Equal("TASK_VALUE", task.VariableValue);
        }

        // =====================================================================
        // UsesEnvironmentSetVariable
        // =====================================================================

        [Fact]
        public void UsesEnvironmentSetVariable_BrokenTask_ResolvesRelativeToCwd()
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

            // Broken task uses Environment.SetEnvironmentVariable — modifies global process state
            Assert.Equal("BROKEN_SET", Environment.GetEnvironmentVariable(varName));
        }

        [Fact]
        public void UsesEnvironmentSetVariable_FixedTask_ResolvesRelativeToProjectDirectory()
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

            // Fixed task uses TaskEnvironment.SetEnvironmentVariable — does NOT modify global state
            Assert.Null(Environment.GetEnvironmentVariable(varName));
            // But the value is stored in the TaskEnvironment
            Assert.Equal("FIXED_SET", taskEnv.GetEnvironmentVariable(varName));
        }

        // =====================================================================
        // ReadsEnvironmentCurrentDirectory
        // =====================================================================

        [Fact]
        public void ReadsEnvironmentCurrentDirectory_BrokenTask_ResolvesRelativeToCwd()
        {
            var projectDir = CreateTempDir();

            var task = new Broken.ReadsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)
            };

            task.Execute();

            // Broken task reads Environment.CurrentDirectory — returns process CWD, not projectDir
            Assert.Equal(Environment.CurrentDirectory, task.CurrentDir);
            Assert.NotEqual(projectDir, task.CurrentDir);
        }

        [Fact]
        public void ReadsEnvironmentCurrentDirectory_FixedTask_ResolvesRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();

            var task = new Fixed.ReadsEnvironmentCurrentDirectory
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir)
            };

            task.Execute();

            // Fixed task reads TaskEnvironment.ProjectDirectory — returns projectDir
            Assert.Equal(projectDir, task.CurrentDir);
        }

        // =====================================================================
        // SetsEnvironmentCurrentDirectory
        // =====================================================================

        [Fact]
        public void SetsEnvironmentCurrentDirectory_BrokenTask_ResolvesRelativeToCwd()
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

            // Broken task sets Environment.CurrentDirectory — modifies global process state
            Assert.Equal(projectDir, Environment.CurrentDirectory);

            // Restore CWD
            Environment.CurrentDirectory = originalCwd;
        }

        [Fact]
        public void SetsEnvironmentCurrentDirectory_FixedTask_ResolvesRelativeToProjectDirectory()
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

            // Fixed task does NOT modify Environment.CurrentDirectory
            Assert.Equal(originalCwd, Environment.CurrentDirectory);
        }
    }
}
