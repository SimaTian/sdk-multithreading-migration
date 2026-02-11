// VIOLATION: Indirect use of Path.GetFullPath through a helper method.
// All path resolution must go through TaskEnvironment, even in helper methods.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class IndirectPathGetFullPath : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string InputPath { get; set; } = string.Empty;

        [Output]
        public string? ResolvedPath { get; set; }

        public override bool Execute()
        {
            ResolvedPath = ResolvePath(InputPath);

            Log.LogMessage(MessageImportance.Normal, $"Resolved '{InputPath}' to '{ResolvedPath}'");

            if (!File.Exists(ResolvedPath))
            {
                Log.LogWarning($"File not found: {ResolvedPath}");
            }

            return true;
        }

        private string ResolvePath(string path)
        {
            // This helper hides the violation — Path.GetFullPath depends on
            // the process-wide current directory, which is shared across threads.
            return Path.GetFullPath(path);
        }
    }
}
