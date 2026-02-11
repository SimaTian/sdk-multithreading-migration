// VIOLATION: Implements IMultiThreadableTask but ignores TaskEnvironment for path resolution, using Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesPathGetFullPath_IgnoresTaskEnv : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string InputPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(InputPath))
            {
                Log.LogError("InputPath is required.");
                return false;
            }

            string absolutePath = Path.GetFullPath(InputPath);
            Log.LogMessage(MessageImportance.Normal, $"Resolved '{InputPath}' to '{absolutePath}'.");

            if (File.Exists(absolutePath))
            {
                long fileSize = new FileInfo(absolutePath).Length;
                Log.LogMessage(MessageImportance.Normal, $"File size: {fileSize} bytes.");
            }
            else
            {
                Log.LogWarning($"File '{absolutePath}' does not exist.");
            }

            return true;
        }
    }
}
