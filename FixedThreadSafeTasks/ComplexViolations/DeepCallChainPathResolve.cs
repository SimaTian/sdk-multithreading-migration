// FIXED: Uses TaskEnvironment.GetAbsolutePath() instead of Path.GetFullPath()
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class DeepCallChainPathResolve : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] InputFiles { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] ProcessedFiles { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (InputFiles == null || InputFiles.Length == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No input files provided; skipping processing.");
                ProcessedFiles = Array.Empty<ITaskItem>();
                return true;
            }

            Log.LogMessage(MessageImportance.Normal, "Beginning file processing for {0} item(s).", InputFiles.Length);
            var results = ProcessFiles(InputFiles);

            ProcessedFiles = results.ToArray();
            Log.LogMessage(MessageImportance.Normal, "Completed processing. {0} file(s) resolved.", ProcessedFiles.Length);
            return !Log.HasLoggedErrors;
        }

        private List<ITaskItem> ProcessFiles(ITaskItem[] items)
        {
            var processed = new List<ITaskItem>();
            foreach (var item in items)
            {
                var validated = ValidateFile(item);
                if (validated != null)
                {
                    processed.Add(validated);
                }
            }
            return processed;
        }

        private ITaskItem? ValidateFile(ITaskItem item)
        {
            string rawPath = item.ItemSpec;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                Log.LogWarning("Skipping item with empty ItemSpec.");
                return null;
            }

            string extension = Path.GetExtension(rawPath);
            if (string.IsNullOrEmpty(extension))
            {
                Log.LogWarning("File '{0}' has no extension; skipping.", rawPath);
                return null;
            }

            string normalized = NormalizePath(rawPath);
            Log.LogMessage(MessageImportance.Low, "Validated '{0}' → '{1}'.", rawPath, normalized);

            var result = new TaskItem(normalized);
            result.SetMetadata("OriginalPath", rawPath);
            result.SetMetadata("FileExtension", extension);
            return result;
        }

        private string NormalizePath(string path)
        {
            string trimmed = path.Trim();
            string unified = trimmed.Replace('/', Path.DirectorySeparatorChar);
            return CleanupPath(unified);
        }

        private string CleanupPath(string path)
        {
            string cleaned = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(cleaned))
                return path;

            return ResolveAbsolutePath(cleaned);
        }

        private string ResolveAbsolutePath(string path)
        {
            if (Path.IsPathRooted(path))
                return path;

            // FIX: Use TaskEnvironment.GetAbsolutePath() for thread safety
            return TaskEnvironment.GetAbsolutePath(path);
        }
    }
}
