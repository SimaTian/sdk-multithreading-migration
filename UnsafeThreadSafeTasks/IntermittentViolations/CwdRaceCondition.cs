// VIOLATION: Modifies Environment.CurrentDirectory (global process state) to resolve relative paths.
// Appears safe because it saves/restores CWD in try/finally, but another thread can change CWD
// between the set and the Path.GetFullPath calls, causing paths to resolve against the wrong directory.
// FIX: Use TaskEnvironment.GetAbsolutePath(relativePath) — never touch Environment.CurrentDirectory.
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.IntermittentViolations
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

            // Set CWD for compatibility with legacy path resolution behavior.
            // We save and restore it to avoid affecting other parts of the build.
            var oldCwd = Environment.CurrentDirectory;
            try
            {
                // VIOLATION: Setting global CWD — another thread can overwrite this immediately.
                Environment.CurrentDirectory = TaskEnvironment.ProjectDirectory;

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
            finally
            {
                // Restore original CWD — but another thread may have already read a stale value.
                Environment.CurrentDirectory = oldCwd;
            }
        }

        /// <summary>
        /// Resolves a single relative path to an absolute path using the current working directory.
        /// BUG: Path.GetFullPath relies on Environment.CurrentDirectory, which can be changed by
        /// another thread between our set and this call.
        /// </summary>
        private string? ResolvePath(string relativePath)
        {
            try
            {
                var normalizedInput = relativePath.Replace('/', Path.DirectorySeparatorChar);

                // VIOLATION: Path.GetFullPath uses the process-global CWD, which is racy.
                var absolutePath = Path.GetFullPath(normalizedInput);
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
