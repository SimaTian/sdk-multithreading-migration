// VIOLATION: Uses Path.GetFullPath for canonicalization instead of TaskEnvironment.GetCanonicalForm. Should use TaskEnvironment.GetAbsolutePath(path) followed by TaskEnvironment.GetCanonicalForm() or just TaskEnvironment.GetCanonicalForm() directly.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesPathGetFullPath_ForCanonicalization : Microsoft.Build.Utilities.Task, IMultiThreadableTask
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

            // Correctly resolve relative path via TaskEnvironment
            string absolutePath = TaskEnvironment.GetAbsolutePath(InputPath);

            // Then incorrectly call Path.GetFullPath for "canonicalization"
            string canonicalPath = Path.GetFullPath(absolutePath);
            Log.LogMessage(MessageImportance.Normal, $"Canonical path: {canonicalPath}");

            if (File.Exists(canonicalPath))
            {
                string content = File.ReadAllText(canonicalPath);
                Log.LogMessage(MessageImportance.Normal, $"Read {content.Length} characters from '{canonicalPath}'.");
            }

            return true;
        }
    }
}
