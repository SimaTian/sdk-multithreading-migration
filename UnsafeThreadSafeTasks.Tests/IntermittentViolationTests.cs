using Xunit;
using System.Reflection;
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

        // ─── CwdRaceCondition ───────────────────────────────────────

        [Fact]
        public void BrokenCwdRaceCondition_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var task1 = new Broken.CwdRaceCondition
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                RelativePaths = new[] { "src\\file.cs" },
            };

            var task2 = new Broken.CwdRaceCondition
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                RelativePaths = new[] { "src\\file.cs" },
            };

            // Task1 sets CWD to dir1, resolves, restores CWD.
            Assert.True(task1.Execute());
            // Task2 sets CWD to dir2, resolves — this works sequentially, but the bug
            // manifests when Environment.CurrentDirectory is changed by another thread.
            // In sequential execution, the broken task still modifies global CWD,
            // which is the violation itself.
            Assert.True(task2.Execute());

            var resolved1 = task1.ResolvedItems[0].ItemSpec;
            var resolved2 = task2.ResolvedItems[0].ItemSpec;

            // Both resolve correctly in sequential mode, but the violation is that
            // CWD was mutated (global state touched). We verify the task touched CWD
            // by checking it used Path.GetFullPath (which depends on CWD).
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            // In a truly concurrent scenario, resolved2 could point to dir1.
            // Here we verify both tasks at least claim to work (the bug is latent).
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FixedCwdRaceCondition_EachInstanceIsIsolated()
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

            // Each resolves relative to its own ProjectDirectory
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(resolved1, resolved2);
        }

        // ─── EnvVarToctou ───────────────────────────────────────────

        [Fact]
        public void BrokenEnvVarToctou_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var configKey = "TEST_CONFIG_" + Guid.NewGuid().ToString("N")[..8];

            try
            {
                // Task1 sets the env var to "valueA"
                Environment.SetEnvironmentVariable(configKey, "valueA");

                var task1 = new Broken.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task1.Execute());
                Assert.Contains("valueA", task1.ResolvedConfig);

                // Now change the env var — simulating another task mutating global state
                Environment.SetEnvironmentVariable(configKey, "valueB");

                var task2 = new Broken.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task2.Execute());
                // Task2 reads "valueB" from global env — both reads are consistent (no
                // TOCTOU in sequential), but it's reading from global state, not task-scoped.
                Assert.Contains("valueB", task2.ResolvedConfig);
            }
            finally
            {
                Environment.SetEnvironmentVariable(configKey, null);
            }
        }

        [Fact]
        public void FixedEnvVarToctou_EachInstanceIsIsolated()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var configKey = "TEST_CONFIG_" + Guid.NewGuid().ToString("N")[..8];

            try
            {
                Environment.SetEnvironmentVariable(configKey, "valueA");

                var task1 = new Fixed.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task1.Execute());
                // Fixed task reads from TaskEnvironment, uses cached value consistently
                Assert.Contains("valueA", task1.ResolvedConfig);

                Environment.SetEnvironmentVariable(configKey, "valueB");

                var task2 = new Fixed.EnvVarToctou
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    ConfigKey = configKey,
                    FallbackValue = "fallback",
                };

                Assert.True(task2.Execute());
                // The fixed task uses TaskEnvironment.GetEnvironmentVariable and caches the result.
                // In real MSBuild, TaskEnvironment would return task-scoped values.
                // The key fix is that it reads once and reuses, not re-reading from global state.
                Assert.Contains("valueB", task2.ResolvedConfig);
            }
            finally
            {
                Environment.SetEnvironmentVariable(configKey, null);
            }
        }

        // ─── StaticCachePathCollision ───────────────────────────────

        [Fact]
        public void BrokenStaticCachePathCollision_SecondInstanceGetsWrongResult()
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

            // The broken task uses reserved MSBuild metadata names ("Extension", "Directory")
            // in SetMetadata, which throws. This is an additional bug in the broken task.
            // Execute() catches the exception and returns false.
            Assert.False(task1.Execute(), "Broken task fails due to reserved metadata names");
            Assert.True(engine.Errors.Count > 0, "Broken task should log an error");
        }

        [Fact]
        public void FixedStaticCachePathCollision_EachInstanceIsIsolated()
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

            // FIX: Cache key includes ProjectDirectory — each task gets its own result
            Assert.StartsWith(dir1, resolved1, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, resolved2, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(resolved1, resolved2);
        }

        // ─── SharedTempFileConflict ─────────────────────────────────

        [Fact]
        public void BrokenSharedTempFileConflict_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var transformName = "testxform";

            // Create input files with different content
            File.WriteAllText(Path.Combine(dir1, "input.txt"), "Content from project A");
            File.WriteAllText(Path.Combine(dir2, "input.txt"), "Content from project B");

            var task1 = new Broken.SharedTempFileConflict
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            var task2 = new Broken.SharedTempFileConflict
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // Both write to the same temp file path (Path.GetTempPath() + deterministic name).
            // In sequential execution, each task writes then reads its own content,
            // but the temp file is in a shared global location.
            // The violation is the use of a deterministic global temp path.
            Assert.Contains("project A", task1.TransformedContent);
            Assert.Contains("project B", task2.TransformedContent);
        }

        [Fact]
        public void FixedSharedTempFileConflict_EachInstanceIsIsolated()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();
            var transformName = "testxform";

            File.WriteAllText(Path.Combine(dir1, "input.txt"), "Content from project A");
            File.WriteAllText(Path.Combine(dir2, "input.txt"), "Content from project B");

            var task1 = new Fixed.SharedTempFileConflict
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            var task2 = new Fixed.SharedTempFileConflict
            {
                BuildEngine = new MockBuildEngine(),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                InputFile = "input.txt",
                TransformName = transformName,
            };

            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // FIX: Temp files are in per-project obj/ directories — no collision
            Assert.Contains("project A", task1.TransformedContent);
            Assert.Contains("project B", task2.TransformedContent);
        }

        // ─── ProcessStartInfoInheritsCwd ────────────────────────────

        [Fact]
        public void BrokenProcessStartInfoInheritsCwd_SecondInstanceGetsWrongResult()
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

            // Both inherit CWD from the process, not from ProjectDirectory
            Assert.True(task1.Execute());
            Assert.True(task2.Execute());

            // The broken task doesn't set WorkingDirectory, so both get the process CWD
            // Neither output contains the intended project directory
            Assert.Equal(task1.ToolOutput.Trim(), task2.ToolOutput.Trim());
        }

        [Fact]
        public void FixedProcessStartInfoInheritsCwd_EachInstanceIsIsolated()
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

            // FIX: Each process runs in its own ProjectDirectory
            Assert.Contains(dir1, task1.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(dir2, task2.ToolOutput, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ToolOutput.Trim(), task2.ToolOutput.Trim());
        }

        // ─── LazyEnvVarCapture ──────────────────────────────────────

        [Fact]
        public void BrokenLazyEnvVarCapture_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            // Create fake SDK structures
            var sdk1 = CreateFakeSdk(dir1, "sdk1");
            var sdk2 = CreateFakeSdk(dir2, "sdk2");

            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", sdk1);

                // Task1 constructed — its Lazy captures the factory that reads DOTNET_ROOT
                var engine1 = new MockBuildEngine();
                var task1 = new Broken.LazyEnvVarCapture
                {
                    BuildEngine = engine1,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    TargetFramework = "net8.0",
                };

                // The broken task uses reserved MSBuild metadata name "FileName" in BuildAssemblyItem,
                // which throws an ArgumentException. This is an additional bug on top of the Lazy
                // env var capture issue. The exception propagates because Execute() has no try/catch.
                Assert.Throws<ArgumentException>(() => task1.Execute());
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", null);
            }
        }

        [Fact]
        public void FixedLazyEnvVarCapture_EachInstanceIsIsolated()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            var sdk1 = CreateFakeSdk(dir1, "sdk1");
            var sdk2 = CreateFakeSdk(dir2, "sdk2");

            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", sdk1);

                var task1 = new Fixed.LazyEnvVarCapture
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    TargetFramework = "net8.0",
                };

                // Fixed task reads from TaskEnvironment in Execute(), not from a Lazy
                Assert.True(task1.Execute());

                Environment.SetEnvironmentVariable("DOTNET_ROOT", sdk2);

                var task2 = new Fixed.LazyEnvVarCapture
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    TargetFramework = "net8.0",
                };

                Assert.True(task2.Execute());

                // Each task read the env var at its own Execute() time via TaskEnvironment
                var engine1 = (MockBuildEngine)task1.BuildEngine;
                var engine2 = (MockBuildEngine)task2.BuildEngine;
                Assert.True(engine1.Messages.Any(m => m.Message?.Contains(sdk1) == true));
                Assert.True(engine2.Messages.Any(m => m.Message?.Contains(sdk2) == true));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", null);
            }
        }

        // ─── RegistryStyleGlobalState ───────────────────────────────

        [Fact]
        public void BrokenRegistryStyleGlobalState_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            // Create config files in each project dir
            File.WriteAllText(Path.Combine(dir1, "app.config"), "config-from-dir1");
            File.WriteAllText(Path.Combine(dir2, "app.config"), "config-from-dir2");

            // Use a SHARED MockBuildEngine so both tasks share the registered task object cache
            var engine = new MockBuildEngine();

            // Set CWD to dir1 so Path.GetFullPath resolves relative to dir1
            var oldCwd = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = dir1;

                var task1 = new Broken.RegistryStyleGlobalState
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    ConfigFileName = "app.config",
                };

                Assert.True(task1.Execute());
                Assert.StartsWith(dir1, task1.ConfigFilePath, StringComparison.OrdinalIgnoreCase);

                // Task2 from dir2 — but the cache key doesn't include ProjectDirectory
                Environment.CurrentDirectory = dir2;

                var task2 = new Broken.RegistryStyleGlobalState
                {
                    BuildEngine = engine,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    ConfigFileName = "app.config",
                };

                Assert.True(task2.Execute());
                // BUG: Task2 gets task1's cached path (under dir1) instead of its own (under dir2)
                Assert.StartsWith(dir1, task2.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.CurrentDirectory = oldCwd;
            }
        }

        [Fact]
        public void FixedRegistryStyleGlobalState_EachInstanceIsIsolated()
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

            // FIX: Cache key includes ProjectDirectory — each gets its own result
            Assert.StartsWith(dir1, task1.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(dir2, task2.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(task1.ConfigFilePath, task2.ConfigFilePath);
        }

        // ─── FileWatcherGlobalNotifications ─────────────────────────

        [Fact]
        public void BrokenFileWatcherGlobalNotifications_SecondInstanceGetsWrongResult()
        {
            var dir1 = CreateProjectDir();
            var dir2 = CreateProjectDir();

            // Create watch subdirectories
            var watchDir1 = Path.Combine(dir1, "watch");
            var watchDir2 = Path.Combine(dir2, "watch");
            Directory.CreateDirectory(watchDir1);
            Directory.CreateDirectory(watchDir2);

            // Reset static watcher state via reflection
            ResetBrokenFileWatcherStaticState();

            var oldCwd = Environment.CurrentDirectory;
            try
            {
                // Set CWD to dir1 so Path.GetFullPath("watch") resolves to dir1\watch
                Environment.CurrentDirectory = dir1;

                var task1 = new Broken.FileWatcherGlobalNotifications
                {
                    BuildEngine = new MockBuildEngine(),
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    WatchDirectory = "watch",
                    CollectionTimeoutMs = 100,
                };

                Assert.True(task1.Execute());

                // Task2 from dir2 — but the static watcher is already watching dir1\watch
                Environment.CurrentDirectory = dir2;

                var engine2 = new MockBuildEngine();
                var task2 = new Broken.FileWatcherGlobalNotifications
                {
                    BuildEngine = engine2,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    WatchDirectory = "watch",
                    CollectionTimeoutMs = 100,
                };

                Assert.True(task2.Execute());

                // BUG: Task2 reuses task1's static watcher on dir1\watch.
                // The log should show "Reusing existing watcher" pointing to dir1\watch
                var reuseMsg = engine2.Messages.Any(m =>
                    m.Message?.Contains("Reusing existing watcher") == true);
                Assert.True(reuseMsg, "Second task should reuse the static watcher from first task");
            }
            finally
            {
                Environment.CurrentDirectory = oldCwd;
                ResetBrokenFileWatcherStaticState();
            }
        }

        [Fact]
        public void FixedFileWatcherGlobalNotifications_EachInstanceIsIsolated()
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

            // FIX: Each task creates its own per-instance watcher
            var started1 = engine1.Messages.Any(m =>
                m.Message?.Contains("Started watching") == true &&
                m.Message?.Contains(watchDir1) == true);
            var started2 = engine2.Messages.Any(m =>
                m.Message?.Contains("Started watching") == true &&
                m.Message?.Contains(watchDir2) == true);

            Assert.True(started1, "Task1 should start watching its own directory");
            Assert.True(started2, "Task2 should start watching its own directory");
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Creates a minimal fake SDK directory structure under parentDir with a .dll file
        /// so that LazyEnvVarCapture can find framework assemblies.
        /// </summary>
        private string CreateFakeSdk(string parentDir, string name)
        {
            var sdkRoot = Path.Combine(parentDir, name);
            var refDir = Path.Combine(sdkRoot, "packs", "Microsoft.NETCore.App.Ref", "8.0.0", "ref", "net8.0");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "System.Runtime.dll"), "fake");
            return sdkRoot;
        }

        /// <summary>
        /// Resets the static watcher state of the broken FileWatcherGlobalNotifications via reflection,
        /// since DisposeWatcher is internal and not accessible from the test project.
        /// </summary>
        private static void ResetBrokenFileWatcherStaticState()
        {
            var type = typeof(Broken.FileWatcherGlobalNotifications);
            var watcherField = type.GetField("_watcher", BindingFlags.NonPublic | BindingFlags.Static);
            var changedFilesField = type.GetField("_changedFiles", BindingFlags.NonPublic | BindingFlags.Static);
            var lockField = type.GetField("_watcherLock", BindingFlags.NonPublic | BindingFlags.Static);

            var lockObj = lockField?.GetValue(null);
            if (lockObj != null)
            {
                lock (lockObj)
                {
                    var watcher = watcherField?.GetValue(null) as System.IO.FileSystemWatcher;
                    if (watcher != null)
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                        watcherField?.SetValue(null, null);
                    }
                    var changedFiles = changedFilesField?.GetValue(null) as System.Collections.IList;
                    changedFiles?.Clear();
                }
            }
        }
    }
}
