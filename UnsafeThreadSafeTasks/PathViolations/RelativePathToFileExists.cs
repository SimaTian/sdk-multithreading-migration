// VIOLATION: File APIs must receive absolute paths. Passes relative path directly to File.Exists and File.ReadAllText without resolving through TaskEnvironment.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
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

            if (File.Exists(FilePath))
            {
                string content = File.ReadAllText(FilePath);
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
