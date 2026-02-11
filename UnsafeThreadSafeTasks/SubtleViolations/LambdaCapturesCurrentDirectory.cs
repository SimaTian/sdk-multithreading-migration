// VIOLATION: Lambda captures Environment.CurrentDirectory instead of
// TaskEnvironment.ProjectDirectory. The current directory is process-global
// and unsafe to read in a multithreaded MSBuild environment.
using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class LambdaCapturesCurrentDirectory : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string[] RelativePaths { get; set; } = Array.Empty<string>();

        [Output]
        public string[] AbsolutePaths { get; set; } = Array.Empty<string>();

        public override bool Execute()
        {
            AbsolutePaths = RelativePaths
                .Select(p => Path.Combine(Environment.CurrentDirectory, p))
                .ToArray();

            foreach (var path in AbsolutePaths)
            {
                Log.LogMessage(MessageImportance.Normal, $"Resolved path: {path}");
            }

            Log.LogMessage(MessageImportance.Normal,
                $"Resolved {AbsolutePaths.Length} paths.");

            return true;
        }
    }
}
