// FIXED: Resolves relative path through TaskEnvironment before passing to File.Exists and File.ReadAllText.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class RelativePathToFileExists : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string FilePath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(FilePath))
            {
                Log.LogError("FilePath is required.");
                return false;
            }

            string resolvedPath = TaskEnvironment.GetAbsolutePath(FilePath);

            if (File.Exists(resolvedPath))
            {
                string content = File.ReadAllText(resolvedPath);
                Log.LogMessage(MessageImportance.Normal, $"File '{FilePath}' contains {content.Length} characters.");
            }
            else
            {
                Log.LogWarning($"File '{FilePath}' was not found.");
            }

            return true;
        }
    }
}
