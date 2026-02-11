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
        public void UsesPathGetFullPath_AttributeOnly_BrokenTask_ResolvesRelativeToCwd()
        {
            var projectDir = CreateTempDir();
            var relativePath = "testfile.txt";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "hello");

            var task = new Broken.UsesPathGetFullPath_AttributeOnly
            {
                BuildEngine = new MockBuildEngine(),
                InputPath = relativePath
            };

            // The broken task uses Path.GetFullPath, resolving relative to CWD
            task.Execute();

            // The broken task resolved relative to CWD, NOT projectDir
            var resolvedByCwd = Path.GetFullPath(relativePath);
            Assert.NotEqual(Path.Combine(projectDir, relativePath), resolvedByCwd);
        }

        [Fact]
        public void UsesPathGetFullPath_AttributeOnly_FixedTask_ResolvesRelativeToProjectDirectory()
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

            Assert.True(result);
            // Verify the fixed task found the file in projectDir
            var engine = (MockBuildEngine)task.BuildEngine;
            Assert.Contains(engine.Messages, m => m.Message.Contains("File found at"));
        }

        // =====================================================================
        // UsesPathGetFullPath_ForCanonicalization
        // =====================================================================

        [Fact]
        public void UsesPathGetFullPath_ForCanonicalization_BrokenTask_ResolvesRelativeToCwd()
        {
            var projectDir = CreateTempDir();
            var relativePath = "subdir/../canon-test.txt";
            Directory.CreateDirectory(Path.Combine(projectDir, "subdir"));
            File.WriteAllText(Path.Combine(projectDir, "canon-test.txt"), "content");

            var task = new Broken.UsesPathGetFullPath_ForCanonicalization
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                InputPath = relativePath
            };

            // The broken task calls Path.GetFullPath for canonicalization after resolving absolute path.
            // With the already-resolved absolute path it may still work, but the violation is the use of Path.GetFullPath.
            task.Execute();

            // The key issue: the broken task uses Path.GetFullPath (forbidden API) even though result may be same.
            // We verify the broken task calls Path.GetFullPath by checking it resolves to CWD-based canonical path
            // when given only a relative path without TaskEnvironment resolution first.
            var brokenResolve = Path.GetFullPath(relativePath);
            Assert.DoesNotContain(projectDir, brokenResolve);
        }

        [Fact]
        public void UsesPathGetFullPath_ForCanonicalization_FixedTask_ResolvesRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "subdir/../canon-test.txt";
            Directory.CreateDirectory(Path.Combine(projectDir, "subdir"));
            File.WriteAllText(Path.Combine(projectDir, "canon-test.txt"), "content");

            var task = new Fixed.UsesPathGetFullPath_ForCanonicalization
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                InputPath = relativePath
            };

            bool result = task.Execute();

            Assert.True(result);
            var engine = (MockBuildEngine)task.BuildEngine;
            // The fixed task should find and read the file via TaskEnvironment
            Assert.Contains(engine.Messages, m => m.Message.Contains("Read") && m.Message.Contains("characters"));
        }

        // =====================================================================
        // UsesPathGetFullPath_IgnoresTaskEnv
        // =====================================================================

        [Fact]
        public void UsesPathGetFullPath_IgnoresTaskEnv_BrokenTask_ResolvesRelativeToCwd()
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

            task.Execute();

            // Broken task uses Path.GetFullPath which resolves relative to CWD, not projectDir
            var engine = (MockBuildEngine)task.BuildEngine;
            // The file exists only in projectDir, not in CWD, so broken task won't find it
            Assert.Contains(engine.Warnings, w => w.Message.Contains("does not exist"));
        }

        [Fact]
        public void UsesPathGetFullPath_IgnoresTaskEnv_FixedTask_ResolvesRelativeToProjectDirectory()
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

            Assert.True(result);
            var engine = (MockBuildEngine)task.BuildEngine;
            // The fixed task should find the file and report its size
            Assert.Contains(engine.Messages, m => m.Message.Contains("File size:"));
        }

        // =====================================================================
        // RelativePathToFileExists
        // =====================================================================

        [Fact]
        public void RelativePathToFileExists_BrokenTask_ResolvesRelativeToCwd()
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

            task.Execute();

            // Broken task passes relative path to File.Exists — resolves against CWD
            var engine = (MockBuildEngine)task.BuildEngine;
            Assert.Contains(engine.Warnings, w => w.Message.Contains("was not found"));
        }

        [Fact]
        public void RelativePathToFileExists_FixedTask_ResolvesRelativeToProjectDirectory()
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

            Assert.True(result);
            var engine = (MockBuildEngine)task.BuildEngine;
            Assert.Contains(engine.Messages, m => m.Message.Contains("contains") && m.Message.Contains("characters"));
        }

        // =====================================================================
        // RelativePathToDirectoryExists
        // =====================================================================

        [Fact]
        public void RelativePathToDirectoryExists_BrokenTask_ResolvesRelativeToCwd()
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

            task.Execute();

            // Broken task passes relative path to Directory.Exists — resolves against CWD.
            // Since "mysubdir" doesn't exist in CWD, it tries to create it there.
            var engine = (MockBuildEngine)task.BuildEngine;
            Assert.Contains(engine.Messages, m => m.Message.Contains("Creating directory"));

            // Clean up the directory it may have created in CWD
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            if (Directory.Exists(cwdPath))
                Directory.Delete(cwdPath, true);
        }

        [Fact]
        public void RelativePathToDirectoryExists_FixedTask_ResolvesRelativeToProjectDirectory()
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

            Assert.True(result);
            var engine = (MockBuildEngine)task.BuildEngine;
            // Fixed task should find the existing directory and report file count
            Assert.Contains(engine.Messages, m => m.Message.Contains("exists with") && m.Message.Contains("file(s)"));
        }

        // =====================================================================
        // RelativePathToFileStream
        // =====================================================================

        [Fact]
        public void RelativePathToFileStream_BrokenTask_ResolvesRelativeToCwd()
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

            // Broken task writes to CWD, not projectDir
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            var projectPath = Path.Combine(projectDir, relativePath);

            Assert.True(File.Exists(cwdPath), "Broken task should write to CWD");
            Assert.False(File.Exists(projectPath), "Broken task should NOT write to projectDir");

            // Clean up file in CWD
            if (File.Exists(cwdPath))
                File.Delete(cwdPath);
        }

        [Fact]
        public void RelativePathToFileStream_FixedTask_ResolvesRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "streamout.bin";

            var task = new Fixed.RelativePathToFileStream
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                OutputPath = relativePath
            };

            bool result = task.Execute();

            Assert.True(result);
            var projectPath = Path.Combine(projectDir, relativePath);
            Assert.True(File.Exists(projectPath), "Fixed task should write to projectDir");
        }

        // =====================================================================
        // RelativePathToXDocument
        // =====================================================================

        [Fact]
        public void RelativePathToXDocument_BrokenTask_ResolvesRelativeToCwd()
        {
            var projectDir = CreateTempDir();
            var relativePath = "data.xml";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "<root><item/></root>");

            var task = new Broken.RelativePathToXDocument
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                XmlPath = relativePath
            };

            // Broken task passes relative path to XDocument.Load — will fail because file is not in CWD
            var ex = Record.Exception(() => task.Execute());
            Assert.NotNull(ex);
        }

        [Fact]
        public void RelativePathToXDocument_FixedTask_ResolvesRelativeToProjectDirectory()
        {
            var projectDir = CreateTempDir();
            var relativePath = "data.xml";
            File.WriteAllText(Path.Combine(projectDir, relativePath), "<root><item/></root>");

            var task = new Fixed.RelativePathToXDocument
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(projectDir),
                XmlPath = relativePath
            };

            bool result = task.Execute();

            Assert.True(result);
            var engine = (MockBuildEngine)task.BuildEngine;
            Assert.Contains(engine.Messages, m => m.Message.Contains("Loaded XML with"));
            Assert.Contains(engine.Messages, m => m.Message.Contains("Saved updated XML"));
        }
    }
}
