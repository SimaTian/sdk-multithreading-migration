// VIOLATION: Attribute-only tasks must not use any forbidden APIs. Path.GetFullPath resolves relative to process CWD.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesPathGetFullPath_AttributeOnly : Microsoft.Build.Utilities.Task
    {
        public string InputPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(InputPath))
            {
                Log.LogError("InputPath is required.");
                return false;
            }

            string resolvedPath = Path.GetFullPath(InputPath);
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
