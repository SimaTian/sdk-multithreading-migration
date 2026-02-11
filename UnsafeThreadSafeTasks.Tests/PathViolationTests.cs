using Xunit;
using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Broken = UnsafeThreadSafeTasks.PathViolations;
using Fixed = FixedThreadSafeTasks.PathViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class PathViolationTests : IDisposable
    {
        private readonly List<string> _tempDirs = new();

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
        }

        // =====================================================================
        // UsesPathGetFullPath_AttributeOnly
        // =====================================================================

        [Fact]
        public void UsesPathGetFullPath_AttributeOnly_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "testfile.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "hello");

            var task = new Broken.UsesPathGetFullPath_AttributeOnly
            {
                BuildEngine = new MockBuildEngine(),
                InputPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find file at projectDir
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("File found at"));
        }

        [Fact]
        public void UsesPathGetFullPath_AttributeOnly_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "testfile.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "hello");

            var task = new Fixed.UsesPathGetFullPath_AttributeOnly
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                InputPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find file at projectDir
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("File found at"));
        }

        // =====================================================================
        // UsesPathGetFullPath_ForCanonicalization
        // =====================================================================

        [Fact]
        public void UsesPathGetFullPath_ForCanonicalization_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "subdir/../canon-test.txt";
            Directory.CreateDirectory(Path.Combine(projectDir, "subdir"));
            File.WriteAllText(Path.Combine(projectDir, "canon-test.txt"), "content");

            var taskEnv = new Infrastructure.TrackingTaskEnvironment { ProjectDirectory = projectDir };
            var task = new Broken.UsesPathGetFullPath_ForCanonicalization
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                InputPath = relativePath
            };

            bool result = task.Execute();

            // The broken task uses Path.GetFullPath() for canonicalization instead of
            // TaskEnvironment.GetCanonicalForm(). Verify GetCanonicalForm was called.
            Assert.True(result);
            Assert.True(taskEnv.GetCanonicalFormCallCount > 0,
                "Broken task should use TaskEnvironment.GetCanonicalForm() instead of Path.GetFullPath()");
        }

        [Fact]
        public void UsesPathGetFullPath_ForCanonicalization_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "subdir/../canon-test.txt";
            Directory.CreateDirectory(Path.Combine(projectDir, "subdir"));
            File.WriteAllText(Path.Combine(projectDir, "canon-test.txt"), "content");

            var taskEnv = new Infrastructure.TrackingTaskEnvironment { ProjectDirectory = projectDir };
            var task = new Fixed.UsesPathGetFullPath_ForCanonicalization
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = taskEnv,
                InputPath = relativePath
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: fixed task uses TaskEnvironment.GetCanonicalForm()
            Assert.True(result);
            Assert.True(taskEnv.GetCanonicalFormCallCount > 0,
                "Fixed task should use TaskEnvironment.GetCanonicalForm() instead of Path.GetFullPath()");
        }

        // =====================================================================
        // UsesPathGetFullPath_IgnoresTaskEnv
        // =====================================================================

        [Fact]
        public void UsesPathGetFullPath_IgnoresTaskEnv_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "ignoretask.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "data");

            var task = new Broken.UsesPathGetFullPath_IgnoresTaskEnv
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                InputPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the file and report its size
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("File size:"));
        }

        [Fact]
        public void UsesPathGetFullPath_IgnoresTaskEnv_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "ignoretask.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "data");

            var task = new Fixed.UsesPathGetFullPath_IgnoresTaskEnv
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                InputPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the file and report its size
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("File size:"));
        }

        // =====================================================================
        // RelativePathToFileExists
        // =====================================================================

        [Fact]
        public void RelativePathToFileExists_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "filecheck.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "exists");

            var task = new Broken.RelativePathToFileExists
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                FilePath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the file and report its content length
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("contains") && m.Message!.Contains("characters"));
        }

        [Fact]
        public void RelativePathToFileExists_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "filecheck.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "exists");

            var task = new Fixed.RelativePathToFileExists
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                FilePath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the file and report its content length
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("contains") && m.Message!.Contains("characters"));
        }

        // =====================================================================
        // RelativePathToDirectoryExists
        // =====================================================================

        [Fact]
        public void RelativePathToDirectoryExists_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "mysubdir";
            Directory.CreateDirectory(Path.Combine(projectDir, relativePath));
            File.WriteAllText(Path.Combine(projectDir, relativePath, "dummy.txt"), "x");

            var task = new Broken.RelativePathToDirectoryExists
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                DirectoryPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the existing directory and report file count
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("exists with") && m.Message!.Contains("file(s)"));

            // Clean up directory that broken task may have created in CWD
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            if (Directory.Exists(cwdPath))
                Directory.Delete(cwdPath, true);
        }

        [Fact]
        public void RelativePathToDirectoryExists_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "mysubdir";
            Directory.CreateDirectory(Path.Combine(projectDir, relativePath));
            File.WriteAllText(Path.Combine(projectDir, relativePath, "dummy.txt"), "x");

            var task = new Fixed.RelativePathToDirectoryExists
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                DirectoryPath = relativePath
            };

            bool result = task.Execute();
            var engine = (MockBuildEngine)task.BuildEngine;

            // Assert CORRECT behavior: task should find the existing directory and report file count
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("exists with") && m.Message!.Contains("file(s)"));
        }

        // =====================================================================
        // RelativePathToFileStream
        // =====================================================================

        [Fact]
        public void RelativePathToFileStream_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "streamout.bin";

            var task = new Broken.RelativePathToFileStream
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                OutputPath = relativePath
            };

            task.Execute();

            var projectPath = Path.Combine(projectDir, relativePath);

            // Assert CORRECT behavior: file should be written to projectDir
            Assert.True(File.Exists(projectPath), "Task should write to projectDir");

            // Clean up file that broken task may have written to CWD
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            if (File.Exists(cwdPath))
                File.Delete(cwdPath);
        }

        [Fact]
        public void RelativePathToFileStream_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "streamout.bin";

            var task = new Fixed.RelativePathToFileStream
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                OutputPath = relativePath
            };

            task.Execute();

            var projectPath = Path.Combine(projectDir, relativePath);

            // Assert CORRECT behavior: file should be written to projectDir
            Assert.True(File.Exists(projectPath), "Task should write to projectDir");
        }

        // =====================================================================
        // RelativePathToXDocument
        // =====================================================================

        [Fact]
        public void RelativePathToXDocument_BrokenTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "data.xml";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "<root><item/></root>");

            var engine = new MockBuildEngine();
            var task = new Broken.RelativePathToXDocument
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                XmlPath = relativePath
            };

            bool result;
            try
            {
                result = task.Execute();
            }
            catch
            {
                result = false;
            }

            // Assert CORRECT behavior: task should load and save the XML successfully
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("Loaded XML with"));
            Assert.Contains(engine.Messages, m => m.Message!.Contains("Saved updated XML"));
        }

        [Fact]
        public void RelativePathToXDocument_FixedTask_ShouldResolveRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "data.xml";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "<root><item/></root>");

            var engine = new MockBuildEngine();
            var task = new Fixed.RelativePathToXDocument
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                XmlPath = relativePath
            };

            bool result = task.Execute();

            // Assert CORRECT behavior: task should load and save the XML successfully
            Assert.True(result);
            Assert.Contains(engine.Messages, m => m.Message!.Contains("Loaded XML with"));
            Assert.Contains(engine.Messages, m => m.Message!.Contains("Saved updated XML"));
        }
    }
}
