// FIXED: Resolves relative path through TaskEnvironment before passing to Directory.Exists and Directory.CreateDirectory.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class RelativePathToDirectoryExists : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string DirectoryPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(DirectoryPath))
            {
                Log.LogError("DirectoryPath is required.");
                return false;
            }

            string resolvedPath = TaskEnvironment.GetAbsolutePath(DirectoryPath);

            if (!Directory.Exists(resolvedPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"Creating directory '{DirectoryPath}'.");
                Directory.CreateDirectory(resolvedPath);
            }
            else
            {
                int fileCount = Directory.GetFiles(resolvedPath).Length;
                Log.LogMessage(MessageImportance.Normal, $"Directory '{DirectoryPath}' exists with {fileCount} file(s).");
            }

            return true;
        }
    }
}
