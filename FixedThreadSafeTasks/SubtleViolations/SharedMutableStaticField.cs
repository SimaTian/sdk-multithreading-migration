using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class SharedMutableStaticField : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        private int _executionCount = 0;
        private string? _lastProcessedFile = null;

        [Required]
        public string InputFile { get; set; } = string.Empty;

        [Output]
        public int ExecutionNumber { get; set; }

        public override bool Execute()
        {
            var resolvedPath = TaskEnvironment.GetAbsolutePath(InputFile);

            _executionCount++;
            ExecutionNumber = _executionCount;
            _lastProcessedFile = resolvedPath;

            Log.LogMessage(MessageImportance.Normal,
                $"Execution #{_executionCount}: processing '{resolvedPath}'");

            if (File.Exists(resolvedPath))
            {
                var size = new FileInfo(resolvedPath).Length;
                Log.LogMessage(MessageImportance.Low,
                    $"File size: {size} bytes (last processed: {_lastProcessedFile})");
            }

            return true;
        }
    }
}
