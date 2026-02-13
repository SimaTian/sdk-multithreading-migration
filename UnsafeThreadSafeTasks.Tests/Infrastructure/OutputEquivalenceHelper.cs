using Microsoft.Build.Framework;

namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    /// <summary>
    /// Compares outputs between Broken (unsafe) and Fixed versions of a task.
    /// When run from a non-CWD ProjectDirectory, the broken version may produce
    /// wrong outputs (resolved against CWD) while fixed produces correct outputs
    /// (resolved against ProjectDirectory).
    /// </summary>
    public static class OutputEquivalenceHelper
    {
        /// <summary>
        /// Runs both task versions from a non-CWD ProjectDirectory.
        /// Returns comparison showing where outputs differ.
        /// </summary>
        public static EquivalenceResult CompareOutputs<TBroken, TFixed>(
            string projectDir,
            Func<string, TaskEnvironment, MockBuildEngine, TBroken> brokenFactory,
            Func<string, TaskEnvironment, MockBuildEngine, TFixed> fixedFactory,
            Func<object, IEnumerable<string>> outputExtractor,
            Action<string>? setupFixtures = null)
            where TBroken : Microsoft.Build.Utilities.Task
            where TFixed : Microsoft.Build.Utilities.Task
        {
            setupFixtures?.Invoke(projectDir);

            // Run broken version
            var brokenEnv = TaskEnvironmentHelper.CreateForTest(projectDir);
            var brokenEngine = new MockBuildEngine();
            var brokenTask = brokenFactory(projectDir, brokenEnv, brokenEngine);
            brokenTask.Execute();
            var brokenOutputs = outputExtractor(brokenTask).ToList();

            // Run fixed version
            var fixedEnv = TaskEnvironmentHelper.CreateForTest(projectDir);
            var fixedEngine = new MockBuildEngine();
            var fixedTask = fixedFactory(projectDir, fixedEnv, fixedEngine);
            fixedTask.Execute();
            var fixedOutputs = outputExtractor(fixedTask).ToList();

            // Compare
            var differences = new List<string>();
            int maxLen = Math.Max(brokenOutputs.Count, fixedOutputs.Count);
            for (int i = 0; i < maxLen; i++)
            {
                var broken = i < brokenOutputs.Count ? brokenOutputs[i] : "<missing>";
                var fix = i < fixedOutputs.Count ? fixedOutputs[i] : "<missing>";
                if (!string.Equals(broken, fix, StringComparison.Ordinal))
                {
                    differences.Add($"[{i}] broken=\"{broken}\" vs fixed=\"{fix}\"");
                }
            }

            return new EquivalenceResult
            {
                OutputsMatch = differences.Count == 0,
                BrokenOutputs = brokenOutputs,
                FixedOutputs = fixedOutputs,
                Differences = differences
            };
        }
    }

    public class EquivalenceResult
    {
        public bool OutputsMatch { get; set; }
        public List<string> BrokenOutputs { get; set; } = new();
        public List<string> FixedOutputs { get; set; } = new();
        public List<string> Differences { get; set; } = new();
    }
}
