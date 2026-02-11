// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.IntermittentViolations
{
    /// <summary>
    /// Monitors a directory for file changes using a static FileSystemWatcher that is shared
    /// across all task instances. The watcher path is resolved with Path.GetFullPath on first
    /// creation, locking it to the first invoking project's CWD. All subsequent invocations
    /// reuse the same watcher and therefore observe changes from the wrong directory.
    /// </summary>
    [MSBuildMultiThreadableTask]
    public class FileWatcherGlobalNotifications : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        private static FileSystemWatcher? _watcher;
        private static readonly object _watcherLock = new();
        private static readonly List<string> _changedFiles = new();

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

            return true;
        }

        /// <summary>
        /// Creates or reuses a static FileSystemWatcher. The lock-and-null-check pattern looks
        /// properly thread-safe, but the bug is semantic: the first invocation resolves
        /// WatchDirectory with Path.GetFullPath (relative to global CWD) and all subsequent
        /// invocations silently reuse that watcher — even if they intended a different directory.
        /// VIOLATION: Uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath, and
        /// shares a single static watcher across all projects.
        /// Fix: Use per-instance watchers with TaskEnvironment.GetAbsolutePath(WatchDirectory).
        /// </summary>
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

                string resolvedDir = Path.GetFullPath(WatchDirectory);
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

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_watcherLock)
            {
                if (!_changedFiles.Contains(e.FullPath))
                {
                    _changedFiles.Add(e.FullPath);
                }
            }
        }

        /// <summary>
        /// Waits for file-change events up to the specified timeout, then returns
        /// the accumulated changed files as ITaskItem[].
        /// </summary>
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

        /// <summary>
        /// Disposes the static watcher. In production code this would be called at build
        /// completion, but because the watcher is static, there is no safe per-project teardown.
        /// </summary>
        internal static void DisposeWatcher()
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
