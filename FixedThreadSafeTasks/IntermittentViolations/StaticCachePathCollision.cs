// FIXED: Cache key includes ProjectDirectory, uses TaskEnvironment.GetAbsolutePath().
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class StaticCachePathCollision : MSBuildTask, IMultiThreadableTask
    {
        // FIX: Cache key now includes ProjectDirectory, so different projects don't collide.
        private static readonly Dictionary<string, string> _resolvedPathCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static int _cacheHits;
        private static int _cacheMisses;

        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string[] InputPaths { get; set; } = Array.Empty<string>();

        [Output]
        public ITaskItem[] ResolvedPaths { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (InputPaths.Length == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No input paths to resolve.");
                ResolvedPaths = Array.Empty<ITaskItem>();
                return true;
            }

            try
            {
                Log.LogMessage(
                    MessageImportance.Normal,
                    "Resolving {0} path(s) for project '{1}'.",
                    InputPaths.Length,
                    TaskEnvironment.ProjectDirectory);

                var results = new List<ITaskItem>(InputPaths.Length);

                foreach (var inputPath in InputPaths)
                {
                    if (string.IsNullOrWhiteSpace(inputPath))
                    {
                        Log.LogWarning("Skipping empty input path.");
                        continue;
                    }

                    var resolved = GetOrResolve(inputPath);
                    if (!ValidateResolvedPath(resolved, inputPath))
                        continue;

                    var item = new TaskItem(resolved);
                    item.SetMetadata("OriginalPath", inputPath);
                    item.SetMetadata("FileExtension", Path.GetExtension(resolved));
                    item.SetMetadata("ContainingDirectory", Path.GetDirectoryName(resolved) ?? string.Empty);

                    results.Add(item);
                }

                ResolvedPaths = results.ToArray();

                LogCacheStatistics();

                Log.LogMessage(
                    MessageImportance.Normal,
                    "Resolved {0} of {1} paths.",
                    results.Count,
                    InputPaths.Length);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private string GetOrResolve(string relativePath)
        {
            // FIX: Include ProjectDirectory in cache key
            var cacheKey = $"{TaskEnvironment.ProjectDirectory}|{relativePath}";

            lock (_cacheLock)
            {
                if (_resolvedPathCache.TryGetValue(cacheKey, out var cached))
                {
                    Interlocked.Increment(ref _cacheHits);
                    Log.LogMessage(MessageImportance.Low, "  Cache hit: '{0}' -> '{1}'", relativePath, cached);
                    return cached;
                }
            }

            // FIX: Use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath
            var absolutePath = Path.GetFullPath(TaskEnvironment.GetAbsolutePath(relativePath));

            lock (_cacheLock)
            {
                if (!_resolvedPathCache.ContainsKey(cacheKey))
                {
                    _resolvedPathCache[cacheKey] = absolutePath;
                    Interlocked.Increment(ref _cacheMisses);
                    Log.LogMessage(MessageImportance.Low, "  Cached:    '{0}' -> '{1}'", relativePath, absolutePath);
                }
                else
                {
                    absolutePath = _resolvedPathCache[cacheKey];
                    Interlocked.Increment(ref _cacheHits);
                    Log.LogMessage(MessageImportance.Low, "  Cache race: '{0}' -> '{1}'", relativePath, absolutePath);
                }
            }

            return absolutePath;
        }

        private bool ValidateResolvedPath(string resolvedPath, string originalPath)
        {
            try
            {
                // Basic validation: ensure the path is well-formed.
                _ = Path.GetFileName(resolvedPath);

                if (resolvedPath.Length > 260)
                {
                    Log.LogWarning("Resolved path exceeds MAX_PATH for '{0}'.", originalPath);
                    return false;
                }

                return true;
            }
            catch (ArgumentException ex)
            {
                Log.LogWarning("Invalid resolved path for '{0}': {1}", originalPath, ex.Message);
                return false;
            }
        }

        private void LogCacheStatistics()
        {
            var hits = Interlocked.CompareExchange(ref _cacheHits, 0, 0);
            var misses = Interlocked.CompareExchange(ref _cacheMisses, 0, 0);
            var total = hits + misses;

            if (total > 0)
            {
                var hitRate = (double)hits / total * 100.0;
                Log.LogMessage(
                    MessageImportance.Low,
                    "Path cache: {0} hits, {1} misses ({2:F1}% hit rate), {3} entries.",
                    hits,
                    misses,
                    hitRate,
                    _resolvedPathCache.Count);
            }
        }
    }
}
