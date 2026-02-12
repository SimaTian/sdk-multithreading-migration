// VIOLATION: Uses Path.GetFullPath for canonicalization instead of TaskEnvironment.GetCanonicalForm.
// Path.GetFullPath resolves relative paths against the process CWD, not ProjectDirectory.
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

            // VIOLATION: Uses Path.GetFullPath directly on the relative input path.
            // This resolves relative to process CWD instead of ProjectDirectory.
            string canonicalPath = Path.GetFullPath(InputPath);
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
