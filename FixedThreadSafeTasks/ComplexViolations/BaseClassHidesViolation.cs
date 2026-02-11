// FIXED: Uses TaskEnvironment.GetAbsolutePath() in base class ResolvePath() instead of Path.GetFullPath()
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.ComplexViolations
{
    public abstract class PathResolvingTaskBase : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        protected string GetMetadata(ITaskItem item, string key)
        {
            string value = item.GetMetadata(key);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        protected void LogVerbose(string message)
        {
            Log.LogMessage(MessageImportance.Low, message);
        }

        protected void LogInfo(string format, params object[] args)
        {
            Log.LogMessage(MessageImportance.Normal, format, args);
        }

        protected bool FileExists(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        protected virtual string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string trimmed = path.Trim();
            if (Path.IsPathRooted(trimmed))
                return trimmed;

            // FIX: Use TaskEnvironment.GetAbsolutePath() for thread safety
            return TaskEnvironment.GetAbsolutePath(trimmed);
        }
    }

    [MSBuildMultiThreadableTask]
    public class DerivedFileProcessor : PathResolvingTaskBase
    {
        [Required]
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public string? ResolvedRoot { get; set; }

        [Output]
        public ITaskItem[] ResolvedSources { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (Sources == null || Sources.Length == 0)
            {
                LogInfo("No sources provided.");
                ResolvedRoot = string.Empty;
                ResolvedSources = Array.Empty<ITaskItem>();
                return true;
            }

            LogInfo("Processing {0} source file(s).", Sources.Length);

            var resolvedItems = new List<ITaskItem>();
            string? commonRoot = null;

            foreach (var source in Sources)
            {
                var resolved = ProcessSource(source);
                if (resolved != null)
                {
                    resolvedItems.Add(resolved);
                    string? dir = Path.GetDirectoryName(resolved.ItemSpec);
                    commonRoot = commonRoot == null ? dir : FindCommonPrefix(commonRoot, dir!);
                }
            }

            ResolvedRoot = commonRoot ?? string.Empty;
            ResolvedSources = resolvedItems.ToArray();
            LogInfo("Resolved root: '{0}', {1} file(s) processed.", ResolvedRoot, ResolvedSources.Length);
            return !Log.HasLoggedErrors;
        }

        private ITaskItem? ProcessSource(ITaskItem item)
        {
            string rawPath = item.ItemSpec;
            string resolved = ResolvePath(rawPath);

            if (string.IsNullOrEmpty(resolved))
            {
                Log.LogWarning("Could not resolve path for '{0}'.", rawPath);
                return null;
            }

            LogVerbose($"Resolved '{rawPath}' → '{resolved}'.");

            var result = new TaskItem(resolved);
            CopyStandardMetadata(item, result);
            result.SetMetadata("OriginalIdentity", rawPath);
            return result;
        }

        private void CopyStandardMetadata(ITaskItem source, ITaskItem destination)
        {
            foreach (string key in new[] { "Link", "CopyToOutputDirectory", "Pack", "BuildAction" })
            {
                string value = GetMetadata(source, key);
                if (!string.IsNullOrEmpty(value))
                {
                    destination.SetMetadata(key, value);
                }
            }
        }

        private static string FindCommonPrefix(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            int lastSep = -1;
            for (int i = 0; i < len; i++)
            {
                if (a[i] != b[i])
                    break;
                if (a[i] == Path.DirectorySeparatorChar)
                    lastSep = i;
            }
            return lastSep >= 0 ? a.Substring(0, lastSep) : string.Empty;
        }
    }
}
