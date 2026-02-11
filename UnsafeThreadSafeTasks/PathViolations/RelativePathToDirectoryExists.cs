// VIOLATION: Directory APIs must receive absolute paths. Passes relative path directly to Directory.Exists and Directory.CreateDirectory without resolving through TaskEnvironment.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
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

            if (!Directory.Exists(DirectoryPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"Creating directory '{DirectoryPath}'.");
                Directory.CreateDirectory(DirectoryPath);
            }
            else
            {
                int fileCount = Directory.GetFiles(DirectoryPath).Length;
                Log.LogMessage(MessageImportance.Normal, $"Directory '{DirectoryPath}' exists with {fileCount} file(s).");
            }

            return true;
        }
    }
}
