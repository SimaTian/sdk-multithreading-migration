// FIXED: GetEffectiveBasePath() returns TaskEnvironment.ProjectDirectory instead of Environment.CurrentDirectory
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class AsyncDelegateViolation : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

        public bool ParallelProcessing { get; set; } = true;

        [Output]
        public ITaskItem[] ProcessedFiles { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (SourceFiles.Length == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No source files to process.");
                return true;
            }

            string projectDir = TaskEnvironment.ProjectDirectory;
            Log.LogMessage(MessageImportance.Normal, $"Processing {SourceFiles.Length} files from '{projectDir}'.");

            var results = new ConcurrentBag<ITaskItem>();
            var errors = new ConcurrentBag<string>();

            if (ParallelProcessing)
            {
                Parallel.ForEach(SourceFiles, sourceItem =>
                {
                    try
                    {
                        string filePath = sourceItem.ItemSpec;
                        var output = ProcessSingleFile(filePath);
                        if (output != null)
                            results.Add(output);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{sourceItem.ItemSpec}: {ex.Message}");
                    }
                });
            }
            else
            {
                foreach (var sourceItem in SourceFiles)
                {
                    try
                    {
                        string filePath = sourceItem.ItemSpec;
                        var output = ProcessSingleFile(filePath);
                        if (output != null)
                            results.Add(output);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{sourceItem.ItemSpec}: {ex.Message}");
                    }
                }
            }

            foreach (string error in errors)
                Log.LogWarning(error);

            ProcessedFiles = results.ToArray();
            Log.LogMessage(MessageImportance.Normal, $"Completed: {ProcessedFiles.Length} succeeded, {errors.Count} failed.");
            return errors.IsEmpty;
        }

        private ITaskItem? ProcessSingleFile(string filePath)
        {
            string basePath = GetEffectiveBasePath();
            string fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(basePath, filePath);

            if (!File.Exists(fullPath))
                return null;

            var info = new FileInfo(fullPath);
            string hash = ComputeFileHash(fullPath);
            string relativePath = fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar)
                : filePath;

            var result = new TaskItem(relativePath);
            result.SetMetadata("ResolvedFullPath", fullPath);
            result.SetMetadata("FileHash", hash);
            result.SetMetadata("FileSize", info.Length.ToString());
            result.SetMetadata("LastWriteTime", info.LastWriteTimeUtc.ToString("o"));
            return result;
        }

        // FIX: returns TaskEnvironment.ProjectDirectory instead of Environment.CurrentDirectory
        private string GetEffectiveBasePath()
        {
            return TaskEnvironment.ProjectDirectory;
        }

        private static string ComputeFileHash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] hashBytes = sha.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
