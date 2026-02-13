using Microsoft.Build.Framework;

namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    /// <summary>
    /// Runs N instances of a task concurrently, each with a different ProjectDirectory.
    /// Verifies output isolation - each instance should only reference its own ProjectDirectory.
    /// </summary>
    public static class ConcurrentTaskRunner
    {
        /// <summary>
        /// Runs taskFactory N times in parallel, each with a unique temp dir as ProjectDirectory.
        /// Returns results: list of (projectDir, taskOutputs) pairs.
        /// taskFactory receives (projectDir, taskEnvironment, buildEngine) and must return the configured task.
        /// outputExtractor receives the executed task and returns a list of output strings to check for isolation.
        /// </summary>
        public static ConcurrentRunResult RunConcurrently<TTask>(
            int instanceCount,
            Func<string, TaskEnvironment, MockBuildEngine, TTask> taskFactory,
            Func<TTask, IEnumerable<string>> outputExtractor,
            Action<string>? setupFixtures = null)
            where TTask : Microsoft.Build.Utilities.Task
        {
            var tempDirs = new List<string>(instanceCount);
            try
            {
                // Create N unique temp dirs
                for (int i = 0; i < instanceCount; i++)
                {
                    var dir = TestHelper.CreateNonCwdTempDirectory();
                    tempDirs.Add(dir);
                    setupFixtures?.Invoke(dir);
                }

                var instances = new InstanceResult[instanceCount];
                var exceptions = new Exception?[instanceCount];
                var threads = new Thread[instanceCount];
                var barrier = new Barrier(instanceCount);

                for (int i = 0; i < instanceCount; i++)
                {
                    int index = i;
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            var projectDir = tempDirs[index];
                            var env = TaskEnvironmentHelper.CreateForTest(projectDir);
                            var engine = new MockBuildEngine();
                            var task = taskFactory(projectDir, env, engine);

                            // Synchronize so all threads execute concurrently
                            barrier.SignalAndWait();

                            task.Execute();

                            var outputs = outputExtractor(task).ToList();
                            instances[index] = new InstanceResult
                            {
                                ProjectDirectory = projectDir,
                                Outputs = outputs
                            };
                        }
                        catch (Exception ex)
                        {
                            exceptions[index] = ex;
                            instances[index] = new InstanceResult
                            {
                                ProjectDirectory = tempDirs[index],
                                Outputs = new List<string>(),
                                ContaminationDetail = $"Exception: {ex.Message}"
                            };
                        }
                    });
                    threads[i].IsBackground = true;
                    threads[i].Start();
                }

                // Wait for all threads to complete
                foreach (var thread in threads)
                    thread.Join();

                // Check isolation: each output should only contain its own projectDir
                int passed = 0;
                int contaminated = 0;

                for (int i = 0; i < instanceCount; i++)
                {
                    var instance = instances[i];
                    if (instance == null) continue;

                    var otherDirs = tempDirs.Where((d, idx) => idx != i).ToList();
                    var leaked = new List<string>();

                    foreach (var output in instance.Outputs)
                    {
                        foreach (var otherDir in otherDirs)
                        {
                            if (output.Contains(otherDir, StringComparison.OrdinalIgnoreCase))
                            {
                                leaked.Add(otherDir);
                            }
                        }
                    }

                    if (leaked.Count > 0)
                    {
                        instance.IsIsolated = false;
                        instance.ContaminationDetail = $"Leaked dirs: {string.Join(", ", leaked.Distinct())}";
                        contaminated++;
                    }
                    else
                    {
                        instance.IsIsolated = true;
                        passed++;
                    }
                }

                return new ConcurrentRunResult
                {
                    InstanceCount = instanceCount,
                    PassedCount = passed,
                    ContaminatedCount = contaminated,
                    Instances = instances.ToList()
                };
            }
            finally
            {
                foreach (var dir in tempDirs)
                    TestHelper.CleanupTempDirectory(dir);
            }
        }
    }

    public class ConcurrentRunResult
    {
        public int InstanceCount { get; set; }
        public int PassedCount { get; set; }
        public int ContaminatedCount { get; set; }
        public List<InstanceResult> Instances { get; set; } = new();
        public bool AllIsolated => ContaminatedCount == 0;
    }

    public class InstanceResult
    {
        public string ProjectDirectory { get; set; } = string.Empty;
        public List<string> Outputs { get; set; } = new();
        public bool IsIsolated { get; set; }
        public string? ContaminationDetail { get; set; }
    }
}
