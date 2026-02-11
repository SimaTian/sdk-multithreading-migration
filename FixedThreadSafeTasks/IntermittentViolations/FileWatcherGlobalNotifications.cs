// FIXED: Uses per-instance watcher with TaskEnvironment.GetAbsolutePath() for path resolution.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class FileWatcherGlobalNotifications : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        // FIX: Per-instance watcher and changed files list instead of static
        private FileSystemWatcher? _watcher;
        private readonly object _watcherLock = new();
        private readonly List<string> _changedFiles = new();

        private const int DefaultCollectionTimeoutMs = 2000;

        [Required]
        public string WatchDirectory { get; set; } = string.Empty;

        public string FileFilter { get; set; } = "*.*";

        public int CollectionTimeoutMs { get; set; } = DefaultCollectionTimeoutMs;

        [Output]
        public ITaskItem[] ChangedFiles { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (string.IsNullOrWhiteSpace(WatchDirectory))
            {
                Log.LogError("WatchDirectory must be specified.");
                return false;
            }

            InitializeWatcher();

            ITaskItem[] collected = CollectChangedFiles(CollectionTimeoutMs);
            ChangedFiles = collected;

            Log.LogMessage(MessageImportance.Normal,
                "Collected {0} changed file(s) matching '{1}'.", collected.Length, FileFilter);

            DisposeWatcher();

            return true;
        }

        private void InitializeWatcher()
        {
            lock (_watcherLock)
            {
                if (_watcher != null)
                {
                    Log.LogMessage(MessageImportance.Low,
                        "Reusing existing watcher on '{0}'.", _watcher.Path);
                    return;
                }

                // FIX: Use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath
                string resolvedDir = TaskEnvironment.GetAbsolutePath(WatchDirectory);
                if (!Directory.Exists(resolvedDir))
                {
                    Log.LogWarning("Watch directory '{0}' does not exist. Creating it.", resolvedDir);
                    Directory.CreateDirectory(resolvedDir);
                }

                _watcher = new FileSystemWatcher(resolvedDir, FileFilter)
                {
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;

                Log.LogMessage(MessageImportance.Normal,
                    "Started watching '{0}' with filter '{1}'.", resolvedDir, FileFilter);
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_watcherLock)
            {
                if (!_changedFiles.Contains(e.FullPath))
                {
                    _changedFiles.Add(e.FullPath);
                }
            }
        }

        private ITaskItem[] CollectChangedFiles(int timeoutMs)
        {
            Thread.Sleep(timeoutMs);

            List<string> snapshot;
            lock (_watcherLock)
            {
                snapshot = new List<string>(_changedFiles);
                _changedFiles.Clear();
            }

            var items = new List<ITaskItem>();
            foreach (string filePath in snapshot)
            {
                var item = new TaskItem(filePath);
                item.SetMetadata("ChangeSource", "FileSystemWatcher");
                item.SetMetadata("Directory", Path.GetDirectoryName(filePath) ?? string.Empty);
                item.SetMetadata("FileName", Path.GetFileName(filePath));
                items.Add(item);
            }

            return items.ToArray();
        }

        internal void DisposeWatcher()
        {
            lock (_watcherLock)
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                    _changedFiles.Clear();
                }
            }
        }
    }
}
