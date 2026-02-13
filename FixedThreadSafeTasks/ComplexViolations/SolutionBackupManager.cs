// FIXED: All filesystem and environment operations use TaskEnvironment equivalents
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class SolutionBackupManager : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string SolutionPath { get; set; } = string.Empty;

        public string BackupRootDirectory { get; set; } = string.Empty;

        public int MaxBackupCount { get; set; } = 5;

        [Required]
        public string Mode { get; set; } = "backup";

        public string FilePatterns { get; set; } = "*.csproj;*.props;*.targets";

        [Output]
        public string BackupId { get; set; } = string.Empty;

        public override bool Execute()
        {
            try
            {
                string resolvedSolutionPath = TaskEnvironment.GetAbsolutePath(SolutionPath);
                Log.LogMessage(MessageImportance.Normal,
                    "SolutionBackupManager running in '{0}' mode for: {1}", Mode, resolvedSolutionPath);

                if (!File.Exists(resolvedSolutionPath))
                {
                    Log.LogError("Solution file not found: {0}", resolvedSolutionPath);
                    return false;
                }

                string backupRoot = ResolveBackupRoot();

                if (string.Equals(Mode, "backup", StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteBackup(resolvedSolutionPath, backupRoot);
                }
                else if (string.Equals(Mode, "restore", StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteRestore(resolvedSolutionPath, backupRoot);
                }
                else
                {
                    Log.LogError("Unknown mode '{0}'. Expected 'backup' or 'restore'.", Mode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private string ResolveBackupRoot()
        {
            if (!string.IsNullOrEmpty(BackupRootDirectory))
            {
                return TaskEnvironment.GetAbsolutePath(BackupRootDirectory);
            }

            // FIX: Use TaskEnvironment.GetEnvironmentVariable instead of Environment.GetFolderPath
            string localAppData = TaskEnvironment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? TaskEnvironment.GetEnvironmentVariable("HOME")
                ?? TaskEnvironment.ProjectDirectory;
            string fallback = System.IO.Path.Combine(localAppData, "SolutionBackups");
            Log.LogMessage(MessageImportance.Low,
                "No BackupRootDirectory specified, falling back to: {0}", fallback);
            return fallback;
        }

        private bool ExecuteBackup(string solutionPath, string backupRoot)
        {
            string solutionDir = System.IO.Path.GetDirectoryName(solutionPath) ?? TaskEnvironment.ProjectDirectory;
            string timestamp = GetBackupTimestamp();
            string solutionName = System.IO.Path.GetFileNameWithoutExtension(solutionPath);
            string backupName = $"{solutionName}_{timestamp}";

            string stagingDir = System.IO.Path.Combine(backupRoot, $".staging_{backupName}");
            Directory.CreateDirectory(stagingDir);
            Log.LogMessage(MessageImportance.Low, "Created staging directory: {0}", stagingDir);

            var filesToBackup = ScanSolutionFiles(solutionDir);
            filesToBackup.Add(solutionPath);

            int copiedCount = 0;
            foreach (string sourceFile in filesToBackup)
            {
                string relativePath = GetRelativePath(solutionDir, sourceFile);
                string destFile = System.IO.Path.Combine(stagingDir, relativePath);

                string destDir = System.IO.Path.GetDirectoryName(destFile) ?? stagingDir;
                Directory.CreateDirectory(destDir);

                File.Copy(sourceFile, destFile, overwrite: true);
                copiedCount++;

                Log.LogMessage(MessageImportance.Low, "Backed up: {0}", relativePath);
            }

            string finalDir = System.IO.Path.Combine(backupRoot, backupName);
            Directory.Move(stagingDir, finalDir);
            Log.LogMessage(MessageImportance.Normal,
                "Backup committed: {0} ({1} files)", finalDir, copiedCount);

            BackupId = backupName;

            RotateOldBackups(backupRoot, solutionName);

            return true;
        }

        private bool ExecuteRestore(string solutionPath, string backupRoot)
        {
            string solutionName = System.IO.Path.GetFileNameWithoutExtension(solutionPath);
            string solutionDir = System.IO.Path.GetDirectoryName(solutionPath) ?? TaskEnvironment.ProjectDirectory;

            string? latestBackup = FindLatestBackup(backupRoot, solutionName);
            if (latestBackup == null)
            {
                Log.LogError("No backups found for solution '{0}' in: {1}", solutionName, backupRoot);
                return false;
            }

            Log.LogMessage(MessageImportance.Normal,
                "Restoring from backup: {0}", latestBackup);

            var backedUpFiles = Directory.EnumerateFiles(latestBackup, "*.*", SearchOption.AllDirectories);
            int restoredCount = 0;

            foreach (string backedUpFile in backedUpFiles)
            {
                string relativePath = GetRelativePath(latestBackup, backedUpFile);
                string restoreDest = System.IO.Path.Combine(solutionDir, relativePath);

                string restoreDir = System.IO.Path.GetDirectoryName(restoreDest) ?? solutionDir;
                Directory.CreateDirectory(restoreDir);

                File.Copy(backedUpFile, restoreDest, overwrite: true);
                restoredCount++;

                Log.LogMessage(MessageImportance.Low, "Restored: {0}", relativePath);
            }

            BackupId = System.IO.Path.GetFileName(latestBackup);
            Log.LogMessage(MessageImportance.Normal,
                "Restore complete. {0} files restored from: {1}", restoredCount, BackupId);

            return true;
        }

        private List<string> ScanSolutionFiles(string solutionDir)
        {
            var files = new List<string>();
            string[] patterns = FilePatterns.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string pattern in patterns)
            {
                string trimmedPattern = pattern.Trim();
                Log.LogMessage(MessageImportance.Low,
                    "Scanning for pattern '{0}' in: {1}", trimmedPattern, solutionDir);

                var matched = Directory.EnumerateFiles(solutionDir, trimmedPattern, SearchOption.AllDirectories);

                foreach (string file in matched)
                {
                    // FIX: uses TaskEnvironment.GetCanonicalForm instead of Path.GetFullPath
                    string canonicalPath = TaskEnvironment.GetCanonicalForm(file);
                    if (!files.Contains(canonicalPath, StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(canonicalPath);
                    }
                }
            }

            Log.LogMessage(MessageImportance.Normal,
                "Found {0} files matching patterns: {1}", files.Count, FilePatterns);
            return files;
        }

        private string? FindLatestBackup(string backupRoot, string solutionName)
        {
            if (!Directory.Exists(backupRoot))
                return null;

            string[] allBackups = Directory.GetDirectories(backupRoot, $"{solutionName}_*");

            if (allBackups.Length == 0)
                return null;

            return allBackups
                .OrderByDescending(d => Directory.GetCreationTime(d))
                .First();
        }

        private void RotateOldBackups(string backupRoot, string solutionName)
        {
            if (MaxBackupCount <= 0)
                return;

            string[] allBackups = Directory.GetDirectories(backupRoot, $"{solutionName}_*");

            if (allBackups.Length <= MaxBackupCount)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Backup count ({0}) within limit ({1}), no rotation needed.",
                    allBackups.Length, MaxBackupCount);
                return;
            }

            var sorted = allBackups
                .OrderByDescending(d => Directory.GetCreationTime(d))
                .ToList();

            var toRemove = sorted.Skip(MaxBackupCount).ToList();

            foreach (string oldBackup in toRemove)
            {
                Log.LogMessage(MessageImportance.Normal,
                    "Removing old backup: {0}", oldBackup);
                Directory.Delete(oldBackup, recursive: true);
            }

            Log.LogMessage(MessageImportance.Normal,
                "Rotated {0} old backups. {1} remaining.", toRemove.Count, MaxBackupCount);
        }

        private string GetBackupTimestamp()
        {
            // FIX: timestamp format is deterministic; no process-global state dependency
            return DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (!basePath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                basePath += System.IO.Path.DirectorySeparatorChar;

            Uri baseUri = new Uri(basePath);
            Uri fullUri = new Uri(fullPath);
            Uri relativeUri = baseUri.MakeRelativeUri(fullUri);

            return Uri.UnescapeDataString(relativeUri.ToString())
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
        }
    }
}
