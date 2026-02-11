// VIOLATION: Uses Path.GetFullPath() inside event handler instead of TaskEnvironment.GetAbsolutePath()
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    public enum ChangeType { Created, Modified, Deleted }

    public class FileChangeEventArgs : EventArgs
    {
        public string FilePath { get; }
        public ChangeType Type { get; }

        public FileChangeEventArgs(string filePath, ChangeType type)
        {
            FilePath = filePath;
            Type = type;
        }
    }

    internal class FileChangeTracker
    {
        public event EventHandler<FileChangeEventArgs>? FileChanged;

        private readonly List<string> _patterns;

        public FileChangeTracker(IEnumerable<string> patterns)
        {
            _patterns = patterns.ToList();
        }

        public void StartTracking() { /* placeholder for real watcher setup */ }
        public void StopTracking() { /* placeholder for cleanup */ }

        public void SimulateChange(string path, ChangeType type = ChangeType.Modified)
        {
            FileChanged?.Invoke(this, new FileChangeEventArgs(path, type));
        }

        public IReadOnlyList<string> Patterns => _patterns;
    }

    [MSBuildMultiThreadableTask]
    public class EventHandlerViolation : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string WatchDirectory { get; set; } = string.Empty;

        public string[] FilePatterns { get; set; } = new[] { "*.*" };

        public int TimeoutMs { get; set; } = 5000;

        [Output]
        public ITaskItem[] ChangedFiles { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            var patterns = SetupPatterns();
            string watchDir = TaskEnvironment.GetAbsolutePath(WatchDirectory);
            Log.LogMessage(MessageImportance.Normal, $"Watching '{watchDir}' with {patterns.Count} pattern(s).");

            var tracker = new FileChangeTracker(patterns);
            var collectedChanges = new List<(string ResolvedPath, ChangeType Type, DateTime Timestamp)>();

            tracker.FileChanged += (sender, e) =>
            {
                // VIOLATION: resolves path via Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
                string resolvedPath = Path.GetFullPath(e.FilePath);

                lock (collectedChanges)
                {
                    collectedChanges.Add((resolvedPath, e.Type, DateTime.UtcNow));
                }

                Log.LogMessage(MessageImportance.Low, $"Detected {e.Type}: {resolvedPath}");
            };

            try
            {
                tracker.StartTracking();

                // Simulate file changes for directories that exist
                if (Directory.Exists(watchDir))
                {
                    foreach (string pattern in patterns)
                    {
                        foreach (string file in Directory.EnumerateFiles(watchDir, pattern, SearchOption.TopDirectoryOnly))
                        {
                            string relative = Path.GetRelativePath(watchDir, file);
                            tracker.SimulateChange(relative);
                        }
                    }
                }

                tracker.StopTracking();
            }
            catch (Exception ex)
            {
                Log.LogError($"File tracking failed: {ex.Message}");
                return false;
            }

            ChangedFiles = BuildOutputItems(collectedChanges);
            CollectResults(collectedChanges.Count);
            return true;
        }

        private List<string> SetupPatterns()
        {
            var result = new List<string>();
            foreach (string raw in FilePatterns)
            {
                string trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    result.Add(trimmed);
            }
            return result.Count > 0 ? result : new List<string> { "*.*" };
        }

        private ITaskItem[] BuildOutputItems(
            List<(string ResolvedPath, ChangeType Type, DateTime Timestamp)> changes)
        {
            var items = new List<ITaskItem>();
            foreach (var (resolvedPath, type, timestamp) in changes)
            {
                var item = new TaskItem(resolvedPath);
                item.SetMetadata("ChangeType", type.ToString());
                item.SetMetadata("DetectedAt", timestamp.ToString("o"));
                items.Add(item);
            }
            return items.ToArray();
        }

        private void CollectResults(int totalChanges)
        {
            Log.LogMessage(MessageImportance.Normal, $"Tracking complete: {totalChanges} change(s) detected.");
        }
    }
}
