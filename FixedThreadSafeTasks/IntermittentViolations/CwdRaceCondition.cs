// FIXED: Uses TaskEnvironment.GetAbsolutePath() instead of setting Environment.CurrentDirectory.
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class CwdRaceCondition : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string[] RelativePaths { get; set; } = Array.Empty<string>();

        [Output]
        public ITaskItem[] ResolvedItems { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (RelativePaths.Length == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No relative paths to resolve.");
                ResolvedItems = Array.Empty<ITaskItem>();
                return true;
            }

            try
            {
                Log.LogMessage(
                    MessageImportance.Normal,
                    "Resolving {0} path(s) relative to '{1}'.",
                    RelativePaths.Length,
                    TaskEnvironment.ProjectDirectory);

                var results = new List<ITaskItem>(RelativePaths.Length);

                foreach (var relativePath in RelativePaths)
                {
                    if (string.IsNullOrWhiteSpace(relativePath))
                    {
                        Log.LogWarning("Skipping empty relative path entry.");
                        continue;
                    }

                    var resolved = ResolvePath(relativePath);
                    if (resolved == null)
                        continue;

                    var item = new TaskItem(resolved);
                    item.SetMetadata("OriginalRelativePath", relativePath);
                    item.SetMetadata("ProjectDirectory", TaskEnvironment.ProjectDirectory);
                    item.SetMetadata("IsRooted", Path.IsPathRooted(relativePath).ToString());

                    results.Add(item);
                }

                ResolvedItems = results.ToArray();

                Log.LogMessage(
                    MessageImportance.Normal,
                    "Resolved {0} of {1} paths successfully.",
                    results.Count,
                    RelativePaths.Length);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private string? ResolvePath(string relativePath)
        {
            try
            {
                var normalizedInput = relativePath.Replace('/', Path.DirectorySeparatorChar);

                // FIX: Use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath
                var absolutePath = TaskEnvironment.GetAbsolutePath(normalizedInput);
                var canonicalPath = Path.GetFullPath(absolutePath);

                Log.LogMessage(
                    MessageImportance.Low,
                    "  '{0}' -> '{1}'",
                    relativePath,
                    canonicalPath);

                if (!canonicalPath.StartsWith(TaskEnvironment.ProjectDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    Log.LogWarning(
                        "Resolved path '{0}' escapes project directory '{1}'.",
                        canonicalPath,
                        TaskEnvironment.ProjectDirectory);
                }

                return canonicalPath;
            }
            catch (ArgumentException ex)
            {
                Log.LogWarning("Invalid path '{0}': {1}", relativePath, ex.Message);
                return null;
            }
        }
    }
}
