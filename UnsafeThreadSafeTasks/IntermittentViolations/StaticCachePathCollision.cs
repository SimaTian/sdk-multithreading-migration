// VIOLATION: Static path-resolution cache keyed by relative path only, not scoped by project.
// When two tasks from different project directories resolve the same relative path (e.g.,
// "obj/output.json"), the first task caches its absolute path and the second task silently
// receives the wrong cached result pointing to the first project's directory.
// The lock makes it look thread-safe — the bug is semantic, not a data race.
// FIX: Key by (ProjectDirectory, relativePath), or use TaskEnvironment.GetAbsolutePath() without caching.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class StaticCachePathCollision : MSBuildTask, IMultiThreadableTask
    {
        // VIOLATION: static cache shared across all task instances — keyed only by relative path.
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
                    item.SetMetadata("Extension", Path.GetExtension(resolved));
                    item.SetMetadata("Directory", Path.GetDirectoryName(resolved) ?? string.Empty);

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

        /// <summary>
        /// Returns the cached absolute path for <paramref name="relativePath"/>, or resolves,
        /// caches, and returns it. The locking prevents data corruption — but the cache key is
        /// only the relative path, so "obj/output.json" from Project A pollutes Project B.
        /// </summary>
        private string GetOrResolve(string relativePath)
        {
            lock (_cacheLock)
            {
                if (_resolvedPathCache.TryGetValue(relativePath, out var cached))
                {
                    Interlocked.Increment(ref _cacheHits);
                    Log.LogMessage(MessageImportance.Low, "  Cache hit: '{0}' -> '{1}'", relativePath, cached);
                    return cached;
                }
            }

            // Resolve the path — this part is correct per-task.
            var absolutePath = Path.GetFullPath(
                Path.Combine(TaskEnvironment.ProjectDirectory, relativePath));

            lock (_cacheLock)
            {
                // Double-check after acquiring the lock. Another thread may have populated it
                // with a value from a DIFFERENT project directory while we were resolving.
                if (!_resolvedPathCache.ContainsKey(relativePath))
                {
                    _resolvedPathCache[relativePath] = absolutePath;
                    Interlocked.Increment(ref _cacheMisses);
                    Log.LogMessage(MessageImportance.Low, "  Cached:    '{0}' -> '{1}'", relativePath, absolutePath);
                }
                else
                {
                    // Another task already cached it — use their value (BUG: may be wrong project).
                    absolutePath = _resolvedPathCache[relativePath];
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
