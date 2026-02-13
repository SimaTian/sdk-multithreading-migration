using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;

using BrokenComplex = UnsafeThreadSafeTasks.ComplexViolations;
using FixedComplex = FixedThreadSafeTasks.ComplexViolations;
using BrokenSubtle = UnsafeThreadSafeTasks.SubtleViolations;
using FixedSubtle = FixedThreadSafeTasks.SubtleViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    /// <summary>
    /// Verifies Fixed tasks can run concurrently without cross-contaminating outputs.
    /// For Broken tasks: demonstrates sequential execution with different ProjectDirectories
    /// produces identical wrong outputs (both resolve to CWD).
    /// DETERMINISTIC - no timing-dependent assertions.
    /// </summary>
    public class ConcurrentIsolationTests
    {
        // ── ArtifactPublisher: Concurrent Fixed instances are isolated ────

        [Fact]
        public void ArtifactPublisher_Fixed_ConcurrentInstancesAreIsolated()
        {
            const int instanceCount = 3;
            var dirs = new string[instanceCount];
            var results = new List<(string dir, string[] publishedFiles)>();
            var threads = new Thread[instanceCount];
            var exceptions = new Exception[instanceCount];

            try
            {
                // Setup: create unique source files in each directory
                for (int i = 0; i < instanceCount; i++)
                {
                    dirs[i] = TestHelper.CreateNonCwdTempDirectory();
                    var srcDir = Path.Combine(dirs[i], "bin");
                    Directory.CreateDirectory(srcDir);
                    File.WriteAllText(Path.Combine(srcDir, $"Lib{i}.dll"), $"content-{i}");
                    Directory.CreateDirectory(Path.Combine(dirs[i], "pub"));
                }

                // Run all instances concurrently
                for (int i = 0; i < instanceCount; i++)
                {
                    int idx = i;
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            var task = new FixedComplex.ArtifactPublisher
                            {
                                BuildEngine = new MockBuildEngine(),
                                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dirs[idx]),
                                SourceDirectories = new ITaskItem[] { new TaskItem("bin") },
                                PublishDirectory = "pub",
                                ManifestPath = "manifest.json",
                                IncludeExtensions = ".dll"
                            };
                            task.Execute();

                            lock (results)
                            {
                                var pubDir = Path.Combine(dirs[idx], "pub");
                                var files = Directory.Exists(pubDir)
                                    ? Directory.GetFiles(pubDir)
                                    : Array.Empty<string>();
                                results.Add((dirs[idx], files));
                            }
                        }
                        catch (Exception ex) { exceptions[idx] = ex; }
                    });
                    threads[i].Start();
                }

                foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

                // Verify no exceptions
                for (int i = 0; i < instanceCount; i++)
                    Assert.Null(exceptions[i]);

                // Verify isolation: each instance's published files are in its own directory
                Assert.Equal(instanceCount, results.Count);
                foreach (var (dir, publishedFiles) in results)
                {
                    foreach (var f in publishedFiles)
                    {
                        Assert.StartsWith(dir, f, StringComparison.OrdinalIgnoreCase);
                        // Verify no contamination from other dirs
                        foreach (var otherDir in dirs.Where(d => d != dir))
                        {
                            Assert.False(f.StartsWith(otherDir, StringComparison.OrdinalIgnoreCase),
                                $"Output {f} is contaminated by another instance's dir {otherDir}");
                        }
                    }
                }
            }
            finally
            {
                foreach (var d in dirs)
                    if (d != null && Directory.Exists(d)) Directory.Delete(d, true);
            }
        }

        [Fact]
        public void ArtifactPublisher_Broken_SequentialInstancesShareGlobalState()
        {
            // DETERMINISTIC: run 2 instances sequentially with different dirs
            // Both will resolve relative paths against CWD, not their ProjectDirectory
            var dir1 = TestHelper.CreateNonCwdTempDirectory();
            var dir2 = TestHelper.CreateNonCwdTempDirectory();
            try
            {
                // Setup source files in both dirs
                foreach (var dir in new[] { dir1, dir2 })
                {
                    var srcDir = Path.Combine(dir, "bin");
                    Directory.CreateDirectory(srcDir);
                    File.WriteAllText(Path.Combine(srcDir, "Lib.dll"), $"content-{dir}");
                }

                var engine1 = new MockBuildEngine();
                var task1 = new BrokenComplex.ArtifactPublisher
                {
                    BuildEngine = engine1,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir1),
                    SourceDirectories = new ITaskItem[] { new TaskItem("bin") },
                    PublishDirectory = "pub",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll"
                };
                task1.Execute();

                var engine2 = new MockBuildEngine();
                var task2 = new BrokenComplex.ArtifactPublisher
                {
                    BuildEngine = engine2,
                    TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dir2),
                    SourceDirectories = new ITaskItem[] { new TaskItem("bin") },
                    PublishDirectory = "pub",
                    ManifestPath = "manifest.json",
                    IncludeExtensions = ".dll"
                };
                task2.Execute();

                // Both should resolve outputs to their own ProjectDirectories
                bool task1InDir1 = File.Exists(Path.Combine(dir1, "pub", "Lib.dll"));
                bool task2InDir2 = File.Exists(Path.Combine(dir2, "pub", "Lib.dll"));
                Assert.True(task1InDir1, "Task 1 should output to dir1/pub");
                Assert.True(task2InDir2, "Task 2 should output to dir2/pub");
            }
            finally
            {
                if (Directory.Exists(dir1)) Directory.Delete(dir1, true);
                if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
            }
        }

        // ── SourceEncodingFixer: Concurrent Fixed instances are isolated ──

        [Fact]
        public void SourceEncodingFixer_Fixed_ConcurrentInstancesAreIsolated()
        {
            const int instanceCount = 3;
            var dirs = new string[instanceCount];
            var results = new List<(string dir, ITaskItem[] modifiedFiles)>();
            var threads = new Thread[instanceCount];
            var exceptions = new Exception[instanceCount];

            try
            {
                for (int i = 0; i < instanceCount; i++)
                {
                    dirs[i] = TestHelper.CreateNonCwdTempDirectory();
                    var srcDir = Path.Combine(dirs[i], "src");
                    Directory.CreateDirectory(srcDir);
                    // Create a file without BOM that the fixer will modify
                    File.WriteAllBytes(Path.Combine(srcDir, $"File{i}.cs"),
                        System.Text.Encoding.UTF8.GetBytes($"// file {i} content"));
                }

                for (int i = 0; i < instanceCount; i++)
                {
                    int idx = i;
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            var task = new FixedSubtle.SourceEncodingFixer
                            {
                                BuildEngine = new MockBuildEngine(),
                                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dirs[idx]),
                                SourceDirectory = "src",
                                FileExtensions = ".cs",
                                TargetEncoding = "utf-8-bom",
                                NormalizeLineEndings = true,
                                CreateBackups = false
                            };
                            task.Execute();

                            lock (results)
                            {
                                results.Add((dirs[idx], task.ModifiedFiles ?? Array.Empty<ITaskItem>()));
                            }
                        }
                        catch (Exception ex) { exceptions[idx] = ex; }
                    });
                    threads[i].Start();
                }

                foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

                for (int i = 0; i < instanceCount; i++)
                    Assert.Null(exceptions[i]);

                // Each instance should only reference its own directory in outputs
                foreach (var (dir, modifiedFiles) in results)
                {
                    foreach (var item in modifiedFiles)
                    {
                        Assert.StartsWith(dir, item.ItemSpec, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            finally
            {
                foreach (var d in dirs)
                    if (d != null && Directory.Exists(d)) Directory.Delete(d, true);
            }
        }

        // ── BuildLogCollector: Concurrent Fixed instances are isolated ────

        [Fact]
        public void BuildLogCollector_Fixed_ConcurrentInstancesAreIsolated()
        {
            const int instanceCount = 3;
            var dirs = new string[instanceCount];
            var results = new List<(string dir, int errors, int warnings, bool reportExists)>();
            var threads = new Thread[instanceCount];
            var exceptions = new Exception[instanceCount];

            try
            {
                for (int i = 0; i < instanceCount; i++)
                {
                    dirs[i] = TestHelper.CreateNonCwdTempDirectory();
                    var logDir = Path.Combine(dirs[i], "logs");
                    Directory.CreateDirectory(logDir);
                    // Each instance gets a different number of errors
                    var logContent = "Build started\n";
                    for (int e = 0; e <= i; e++)
                        logContent += $"foo.cs({e},1): error CS{e:D4}: Error {e} in instance {i}\n";
                    logContent += "bar.cs(1,1): warning CS9999: Common warning\nBuild complete";
                    File.WriteAllText(Path.Combine(logDir, "build.log"), logContent);
                }

                for (int i = 0; i < instanceCount; i++)
                {
                    int idx = i;
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            var reportPath = Path.Combine(dirs[idx], "report.html");
                            var task = new FixedComplex.BuildLogCollector
                            {
                                BuildEngine = new MockBuildEngine(),
                                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(dirs[idx]),
                                LogDirectories = new ITaskItem[] { new TaskItem("logs") },
                                OutputReportPath = "report.html",
                                IncludeWarnings = true
                            };
                            task.Execute();

                            lock (results)
                            {
                                results.Add((dirs[idx], task.TotalErrors, task.TotalWarnings,
                                    File.Exists(reportPath)));
                            }
                        }
                        catch (Exception ex) { exceptions[idx] = ex; }
                    });
                    threads[i].Start();
                }

                foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

                for (int i = 0; i < instanceCount; i++)
                    Assert.Null(exceptions[i]);

                // Each instance should have its own error count (1, 2, 3)
                // and its report in its own directory
                Assert.Equal(instanceCount, results.Count);
                foreach (var (dir, errors, warnings, reportExists) in results)
                {
                    Assert.True(reportExists, $"Report should exist in {dir}");
                    Assert.True(errors >= 1, $"Each instance should have at least 1 error");
                    Assert.Equal(1, warnings);
                }

                // Verify error counts differ (proves isolation, not sharing)
                var errorCounts = results.Select(r => r.errors).OrderBy(x => x).ToList();
                Assert.True(errorCounts.Distinct().Count() >= 2,
                    "Different instances should produce different error counts (isolation proof)");
            }
            finally
            {
                foreach (var d in dirs)
                    if (d != null && Directory.Exists(d)) Directory.Delete(d, true);
            }
        }
    }
}
