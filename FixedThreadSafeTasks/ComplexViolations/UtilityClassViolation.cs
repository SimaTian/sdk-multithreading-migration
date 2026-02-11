// FIXED: PathUtilities.MakeAbsolute uses basePath parameter instead of Path.GetFullPath()
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.ComplexViolations
{
    internal static class PathUtilities
    {
        public static string NormalizeSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        public static string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        public static bool IsValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                _ = Path.GetFileName(path);
                return path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
            }
            catch
            {
                return false;
            }
        }

        public static string MakeAbsolute(string path, string basePath)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = NormalizeSeparators(path.Trim());
            if (Path.IsPathRooted(normalized))
                return normalized;

            // FIX: Use basePath parameter instead of Path.GetFullPath()
            return Path.Combine(basePath, normalized);
        }

        public static string CombineAndNormalize(string basePath, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return EnsureTrailingSlash(basePath);

            return MakeAbsolute(relativePath, basePath);
        }
    }

    [MSBuildMultiThreadableTask]
    public class OutputDirectoryResolver : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string OutputDirectory { get; set; } = string.Empty;

        public string IntermediateDirectory { get; set; } = string.Empty;

        public ITaskItem[] ProjectReferences { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public string? ResolvedOutputDirectory { get; set; }

        [Output]
        public string? ResolvedIntermediateDirectory { get; set; }

        [Output]
        public ITaskItem[] ResolvedProjectReferences { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Resolving output directories.");

            string projectDir = TaskEnvironment?.ProjectDirectory ?? string.Empty;

            ResolvedOutputDirectory = ResolveDirectory(OutputDirectory, projectDir);
            ResolvedIntermediateDirectory = !string.IsNullOrEmpty(IntermediateDirectory)
                ? ResolveDirectory(IntermediateDirectory, projectDir)
                : ResolvedOutputDirectory;

            Log.LogMessage(MessageImportance.Low,
                "Output='{0}', Intermediate='{1}'.", ResolvedOutputDirectory, ResolvedIntermediateDirectory);

            ResolvedProjectReferences = ResolveReferences(projectDir);

            Log.LogMessage(MessageImportance.Normal,
                "Directory resolution complete. {0} reference(s) processed.",
                ResolvedProjectReferences?.Length ?? 0);

            return !Log.HasLoggedErrors;
        }

        private string ResolveDirectory(string directory, string projectDir)
        {
            if (!PathUtilities.IsValidPath(directory))
            {
                Log.LogWarning("Invalid directory path: '{0}'.", directory);
                return string.Empty;
            }

            string resolved = PathUtilities.CombineAndNormalize(projectDir, directory);
            return PathUtilities.EnsureTrailingSlash(resolved);
        }

        private ITaskItem[] ResolveReferences(string projectDir)
        {
            if (ProjectReferences == null || ProjectReferences.Length == 0)
                return Array.Empty<ITaskItem>();

            var results = new List<ITaskItem>();
            foreach (var reference in ProjectReferences)
            {
                var resolved = ResolveReference(reference, projectDir);
                if (resolved != null)
                    results.Add(resolved);
            }
            return results.ToArray();
        }

        private ITaskItem? ResolveReference(ITaskItem reference, string projectDir)
        {
            string refPath = reference.ItemSpec;
            if (!PathUtilities.IsValidPath(refPath))
            {
                Log.LogWarning("Skipping invalid project reference: '{0}'.", refPath);
                return null;
            }

            string resolvedPath = PathUtilities.CombineAndNormalize(projectDir, refPath);
            Log.LogMessage(MessageImportance.Low, "Reference '{0}' → '{1}'.", refPath, resolvedPath);

            var result = new TaskItem(resolvedPath);
            result.SetMetadata("OriginalItemSpec", refPath);

            string projectName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (!string.IsNullOrEmpty(projectName))
                result.SetMetadata("ProjectName", projectName);

            return result;
        }
    }
}
