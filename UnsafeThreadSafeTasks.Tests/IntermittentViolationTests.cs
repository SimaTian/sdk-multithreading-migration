using Xunit;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Broken = UnsafeThreadSafeTasks.IntermittentViolations;
using Fixed = FixedThreadSafeTasks.IntermittentViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class IntermittentViolationTests : IDisposable
    {
        private readonly List<string> _tempDirs = new();

        private string CreateProjectDir()
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

        // --- CwdRaceCondition ---

        [Fact]
        public void CwdRaceCondition_Broken_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var originalCwd = Environment.CurrentDirectory;

            // The broken task sets Environment.CurrentDirectory = ProjectDirectory then restores it.
            // We detect this by checking CWD from a concurrent thread during execution.
            var cwdChanged = false;
            var executing = true;

            var monitor = new Thread(() =>
            {
                while (Volatile.Read(ref executing))
                {
                    if (!Environment.CurrentDirectory.Equals(originalCwd, StringComparison.OrdinalIgnoreCase))
                    {
                        cwdChanged = true;
                        break;
                    }
                }
            });
            monitor.IsBackground = true;
            monitor.Start();

            // Run task multiple times to increase chance of catching the CWD change
            for (int i = 0; i < 50 && !cwdChanged; i++)
            {
                var task = new Broken.CwdRaceCondition
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    RelativePaths = new[] { "src\\file.cs", "lib\\helper.cs", "tests\\test.cs" },
                };
                task.Execute();
            }

            Volatile.Write(ref executing, false);
            monitor.Join(2000);

            // The broken task should NOT modify Environment.CurrentDirectory.
            // A correct implementation uses TaskEnvironment.GetAbsolutePath() directly.
            Assert.False(cwdChanged,
                $"Task must not modify Environment.CurrentDirectory. The broken task sets CWD to ProjectDirectory ('{dir1}') during execution.");

            // Restore CWD in case it was changed
            Environment.CurrentDirectory = originalCwd;
        }

        [Fact]
        public void CwdRaceCondition_Fixed_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var task1 = new Fixed.CwdRaceCondition
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                RelativePaths = new[] { "src\\file.cs" },
            };

            var task2 = new Fixed.CwdRaceCondition
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                RelativePaths = new[] { "src\\file.cs" },
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            var resolved1 = task1.ResolvedItems[0].ItemSpec;
            var resolved2 = task2.ResolvedItems[0].ItemSpec;

            // Assert CORRECT behavior: each resolves to its own ProjectDirectory
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(resolved1, resolved2);
        }

        // --- EnvVarToctou ---

        [Fact]
        public void EnvVarToctou_Broken_ShouldUseTaskScopedEnvVars()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var configKey = "TEST_CONFIG_" + Guid.NewGuid().ToString("N")[..8];

            try
            {
                var taskEnv1 = TaskEnvironmentHelper.CreateForTest(dir1);
                taskEnv1.SetEnvironmentVariable(configKey, "valueA");

                var task1 = new Broken.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv1,
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task1.Execute());

                var taskEnv2 = TaskEnvironmentHelper.CreateForTest(dir2);
                taskEnv2.SetEnvironmentVariable(configKey, "valueB");

                var task2 = new Broken.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv2,
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task2.Execute());

                // Assert CORRECT behavior: each reads its own TaskEnvironment value
                Assert.Contains("valueA", task1.ResolvedConfig);
                Assert.Contains("valueB", task2.ResolvedConfig);
            }
            finally
            {
                Environment.SetEnvironmentVariable(configKey, null);
            }
        }

        [Fact]
        public void EnvVarToctou_Fixed_ShouldUseTaskScopedEnvVars()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var configKey = "TEST_CONFIG_" + Guid.NewGuid().ToString("N")[..8];

            try
            {
                var taskEnv1 = TaskEnvironmentHelper.CreateForTest(dir1);
                taskEnv1.SetEnvironmentVariable(configKey, "valueA");

                var task1 = new Fixed.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv1,
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task1.Execute());

                var taskEnv2 = TaskEnvironmentHelper.CreateForTest(dir2);
                taskEnv2.SetEnvironmentVariable(configKey, "valueB");

                var task2 = new Fixed.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv2,
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task2.Execute());

                // Assert CORRECT behavior: each reads its own TaskEnvironment value
                Assert.Contains("valueA", task1.ResolvedConfig);
                Assert.Contains("valueB", task2.ResolvedConfig);
            }
            finally
            {
                Environment.SetEnvironmentVariable(configKey, null);
            }
        }

        // --- StaticCachePathCollision ---

        [Fact]
        public void StaticCachePathCollision_Broken_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var engine = new MockBuildEngine();

            var task1 = new Broken.StaticCachePathCollision
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputPaths = new[] { "obj\\output.json" },
            };

            var task2 = new Broken.StaticCachePathCollision
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputPaths = new[] { "obj\\output.json" },
            };

            // Assert CORRECT behavior: both should succeed
            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            var resolved1 = task1.ResolvedPaths[0].ItemSpec;
            var resolved2 = task2.ResolvedPaths[0].ItemSpec;

            // Assert CORRECT behavior: each resolves to its own ProjectDirectory
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(resolved1, resolved2);
        }

        [Fact]
        public void StaticCachePathCollision_Fixed_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var engine = new MockBuildEngine();

            var task1 = new Fixed.StaticCachePathCollision
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputPaths = new[] { "obj\\output.json" },
            };

            var task2 = new Fixed.StaticCachePathCollision
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputPaths = new[] { "obj\\output.json" },
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            var resolved1 = task1.ResolvedPaths[0].ItemSpec;
            var resolved2 = task2.ResolvedPaths[0].ItemSpec;

            // Assert CORRECT behavior: each resolves to its own ProjectDirectory
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(resolved1, resolved2);
        }

        // --- SharedTempFileConflict ---

        [Fact]
        public void SharedTempFileConflict_Broken_ShouldUseIsolatedTempFiles()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var transformName = "testxform";

            File.WriteAllText(Path.Combine(dir1, "input.txt"), "Content from project A");
            File.WriteAllText(Path.Combine(dir2, "input.txt"), "Content from project B");

            var engine1 = new MockBuildEngine();
            var engine2 = new MockBuildEngine();

            var task1 = new Broken.SharedTempFileConflict
            {
                BuildEngine = engine1,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            var task2 = new Broken.SharedTempFileConflict
            {
                BuildEngine = engine2,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Each task should have its own content, not corrupted by the other
            Assert.Contains("Content from project A", task1.TransformedContent);
            Assert.Contains("Content from project B", task2.TransformedContent);

            // Verify temp files are scoped to the project directory, not global temp
            Assert.Contains(engine1.Messages, m => m.Message!.Contains("Wrote intermediate result") && m.Message!.Contains(dir1));
            Assert.Contains(engine2.Messages, m => m.Message!.Contains("Wrote intermediate result") && m.Message!.Contains(dir2));
        }

        [Fact]
        public void SharedTempFileConflict_Fixed_ShouldUseIsolatedTempFiles()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var transformName = "testxform";

            File.WriteAllText(Path.Combine(dir1, "input.txt"), "Content from project A");
            File.WriteAllText(Path.Combine(dir2, "input.txt"), "Content from project B");

            var engine1 = new MockBuildEngine();
            var engine2 = new MockBuildEngine();

            var task1 = new Fixed.SharedTempFileConflict
            {
                BuildEngine = engine1,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            var task2 = new Fixed.SharedTempFileConflict
            {
                BuildEngine = engine2,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Each task should have its own content, not corrupted by the other
            Assert.Contains("Content from project A", task1.TransformedContent);
            Assert.Contains("Content from project B", task2.TransformedContent);

            // Verify temp files are scoped to the project directory, not global temp
            Assert.Contains(engine1.Messages, m => m.Message!.Contains("Wrote intermediate result") && m.Message!.Contains(dir1));
            Assert.Contains(engine2.Messages, m => m.Message!.Contains("Wrote intermediate result") && m.Message!.Contains(dir2));
        }

        // --- ProcessStartInfoInheritsCwd ---

        [Fact]
        public void ProcessStartInfoInheritsCwd_Broken_ShouldRunInProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var task1 = new Broken.ProcessStartInfoInheritsCwd
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                ToolName = "cmd.exe",
                Arguments = "/c cd",
                TimeoutMilliseconds = 5000,
            };

            var task2 = new Broken.ProcessStartInfoInheritsCwd
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                ToolName = "cmd.exe",
                Arguments = "/c cd",
                TimeoutMilliseconds = 5000,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Assert CORRECT behavior: each process runs in its own ProjectDirectory
            Assert.Contains(dir1, task1.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(dir2, task2.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ToolOutput.Trim(), task2.ToolOutput.Trim());
        }

        [Fact]
        public void ProcessStartInfoInheritsCwd_Fixed_ShouldRunInProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var task1 = new Fixed.ProcessStartInfoInheritsCwd
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                ToolName = "cmd.exe",
                Arguments = "/c cd",
                TimeoutMilliseconds = 5000,
            };

            var task2 = new Fixed.ProcessStartInfoInheritsCwd
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                ToolName = "cmd.exe",
                Arguments = "/c cd",
                TimeoutMilliseconds = 5000,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Assert CORRECT behavior: each process runs in its own ProjectDirectory
            Assert.Contains(dir1, task1.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(dir2, task2.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ToolOutput.Trim(), task2.ToolOutput.Trim());
        }

        // --- LazyEnvVarCapture ---

        [Fact]
        public void LazyEnvVarCapture_Broken_ShouldUseTaskEnvironment()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var sdk1 = CreateFakeSdk(dir1, "sdk1");
            var sdk2 = CreateFakeSdk(dir2, "sdk2");

            try
            {
                var taskEnv1 = TaskEnvironmentHelper.CreateForTest(dir1);
                taskEnv1.SetEnvironmentVariable("DOTNET_ROOT", sdk1);

                var engine1 = new MockBuildEngine();
                var task1 = new Broken.LazyEnvVarCapture
                {
                    BuildEngine = engine1,
                    TaskEnvironment = taskEnv1,
                    TargetFramework = "net8.0",
                };

                // Assert CORRECT behavior: should execute successfully
                Assert.True(task1.Execute());
                Assert.Contains(engine1.Messages!, m => m.Message != null && m.Message.Contains(sdk1));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", null);
            }
        }

        [Fact]
        public void LazyEnvVarCapture_Fixed_ShouldUseTaskEnvironment()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var sdk1 = CreateFakeSdk(dir1, "sdk1");
            var sdk2 = CreateFakeSdk(dir2, "sdk2");

            try
            {
                var taskEnv1 = TaskEnvironmentHelper.CreateForTest(dir1);
                taskEnv1.SetEnvironmentVariable("DOTNET_ROOT", sdk1);

                var task1 = new Fixed.LazyEnvVarCapture
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv1,
                    TargetFramework = "net8.0",
                };

                Assert.True(task1.Execute());

                var taskEnv2 = TaskEnvironmentHelper.CreateForTest(dir2);
                taskEnv2.SetEnvironmentVariable("DOTNET_ROOT", sdk2);

                var task2 = new Fixed.LazyEnvVarCapture
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = taskEnv2,
                    TargetFramework = "net8.0",
                };

                Assert.True(task2.Execute());

                // Assert CORRECT behavior: each task reads its own SDK path
                var engine1 = (MockBuildEngine)task1.BuildEngine;
                var engine2 = (MockBuildEngine)task2.BuildEngine;
                Assert.Contains(engine1.Messages!, m => m.Message != null && m.Message.Contains(sdk1));
                Assert.Contains(engine2.Messages!, m => m.Message != null && m.Message.Contains(sdk2));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", null);
            }
        }

        // --- RegistryStyleGlobalState ---

        [Fact]
        public void RegistryStyleGlobalState_Broken_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            File.WriteAllText(Path.Combine(dir1, "app.config"), "config-from-dir1");
            File.WriteAllText(Path.Combine(dir2, "app.config"), "config-from-dir2");

            var engine = new MockBuildEngine();

            var task1 = new Broken.RegistryStyleGlobalState
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                ConfigFileName = "app.config",
            };

            var task2 = new Broken.RegistryStyleGlobalState
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                ConfigFileName = "app.config",
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Assert CORRECT behavior: each task resolves config to its own ProjectDir
            Assert.StartsWith(dir1, task1.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, task2.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ConfigFilePath, task2.ConfigFilePath);
        }

        [Fact]
        public void RegistryStyleGlobalState_Fixed_ShouldResolveToOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            File.WriteAllText(Path.Combine(dir1, "app.config"), "config-from-dir1");
            File.WriteAllText(Path.Combine(dir2, "app.config"), "config-from-dir2");

            var engine = new MockBuildEngine();

            var task1 = new Fixed.RegistryStyleGlobalState
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                ConfigFileName = "app.config",
            };

            var task2 = new Fixed.RegistryStyleGlobalState
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                ConfigFileName = "app.config",
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Assert CORRECT behavior: each task resolves config to its own ProjectDir
            Assert.StartsWith(dir1, task1.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, task2.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ConfigFilePath, task2.ConfigFilePath);
        }

        // --- FileWatcherGlobalNotifications ---

        [Fact]
        public void FileWatcherGlobalNotifications_Broken_ShouldWatchOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var watchDir1 = Path.Combine(dir1, "watch");
            var watchDir2 = Path.Combine(dir2, "watch");
            Directory.CreateDirectory(watchDir1);
            Directory.CreateDirectory(watchDir2);

            try
            {
                // Ensure clean static state
                Broken.FileWatcherGlobalNotifications.DisposeWatcher();

                var engine1 = new MockBuildEngine();
                var engine2 = new MockBuildEngine();

                var task1 = new Broken.FileWatcherGlobalNotifications
                {
                    BuildEngine = engine1,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    WatchDirectory = "watch",
                    CollectionTimeoutMs = 100,
                };

                var task2 = new Broken.FileWatcherGlobalNotifications
                {
                    BuildEngine = engine2,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    WatchDirectory = "watch",
                    CollectionTimeoutMs = 100,
                };

                Assert.True(task1.Execute());
                Assert.True(task2.Execute());

                // Verify task1 is watching its own project directory
                Assert.Contains(engine1.Messages, m => m.Message!.Contains(watchDir1));
            }
            finally
            {
                Broken.FileWatcherGlobalNotifications.DisposeWatcher();
            }
        }

        [Fact]
        public void FileWatcherGlobalNotifications_Fixed_ShouldWatchOwnProjectDir()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var watchDir1 = Path.Combine(dir1, "watch");
            var watchDir2 = Path.Combine(dir2, "watch");
            Directory.CreateDirectory(watchDir1);
            Directory.CreateDirectory(watchDir2);

            var engine1 = new MockBuildEngine();
            var engine2 = new MockBuildEngine();

            var task1 = new Fixed.FileWatcherGlobalNotifications
            {
                BuildEngine = engine1,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                WatchDirectory = "watch",
                CollectionTimeoutMs = 100,
            };

            var task2 = new Fixed.FileWatcherGlobalNotifications
            {
                BuildEngine = engine2,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                WatchDirectory = "watch",
                CollectionTimeoutMs = 100,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Verify task1 is watching its own project directory
            Assert.Contains(engine1.Messages, m => m.Message!.Contains(watchDir1));
        }

        // --- Helpers ---

        private string CreateFakeSdk(string parentDir, string name)
        {
            var sdkRoot = Path.Combine(parentDir, name);
            var refDir = Path.Combine(sdkRoot, "packs", "Microsoft.NETCore.App.Ref", "8.0.0", "ref", "net8.0");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "System.Runtime.dll"), "fake");
            return sdkRoot;
        }
    }
}
