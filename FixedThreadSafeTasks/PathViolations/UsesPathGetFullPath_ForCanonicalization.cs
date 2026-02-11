// FIXED: Uses TaskEnvironment.GetCanonicalForm instead of Path.GetFullPath for canonicalization.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.PathViolations
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

            // Use TaskEnvironment.GetCanonicalForm instead of Path.GetFullPath
            string canonicalPath = TaskEnvironment.GetCanonicalForm(InputPath);
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
