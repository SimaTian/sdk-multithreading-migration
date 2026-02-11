// FIXED: Attribute-only task now implements IMultiThreadableTask and uses TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesPathGetFullPath_AttributeOnly : Microsoft.Build.Utilities.Task, IMultiThreadableTask
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

            string resolvedPath = TaskEnvironment.GetAbsolutePath(InputPath);
            Log.LogMessage(MessageImportance.Normal, $"Resolved path: {resolvedPath}");

            if (File.Exists(resolvedPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"File found at '{resolvedPath}'.");
            }
            else
            {
                Log.LogMessage(MessageImportance.Normal, $"File not found at '{resolvedPath}'.");
            }

            return true;
        }
    }
}
