// FIXED: Uses TaskEnvironment.GetProcessStartInfo() for all process creation
// and TaskEnvironment.GetCanonicalForm() for path resolution.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ProcessViolations
{
    /// <summary>
    /// Selectively cleans build output directories while preserving specified files
    /// (e.g., user settings, generated license files). Detects and handles locked files
    /// by spawning external processes to check locks and force-delete.
    /// </summary>
    [MSBuildMultiThreadableTask]
    public class OutputCleanManager : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] OutputDirectories { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>
        /// Semicolon-delimited glob patterns for files to preserve, e.g. "*.license;*.user".
        /// </summary>
        public string PreservePatterns { get; set; } = string.Empty;

        public bool HandleLockedFiles { get; set; } = true;

        public bool ForceClean { get; set; } = false;

        [Output]
        public int CleanedFiles { get; set; }

        [Output]
        public int SkippedFiles { get; set; }

        public override bool Execute()
        {
            try
            {
                CleanedFiles = 0;
                SkippedFiles = 0;

                string[] patterns = string.IsNullOrWhiteSpace(PreservePatterns)
                    ? Array.Empty<string>()
                    : PreservePatterns.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                Log.LogMessage(MessageImportance.Normal,
                    "OutputCleanManager: cleaning {0} directories, preserving patterns: [{1}]",
                    OutputDirectories.Length, string.Join(", ", patterns));

                foreach (ITaskItem dirItem in OutputDirectories)
                {
                    string dir = dirItem.ItemSpec;

                    // FIXED: Use TaskEnvironment.GetCanonicalForm() instead of Path.GetFullPath()
                    string resolvedDir = TaskEnvironment.GetCanonicalForm(dir);

                    if (!Directory.Exists(resolvedDir))
                    {
                        Log.LogMessage(MessageImportance.Low,
                            "Directory does not exist, skipping: {0}", resolvedDir);
                        continue;
                    }

                    CleanDirectory(resolvedDir, patterns);
                }

                Log.LogMessage(MessageImportance.Normal,
                    "OutputCleanManager complete. Cleaned: {0}, Skipped: {1}",
                    CleanedFiles, SkippedFiles);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private void CleanDirectory(string dir, string[] preservePatterns)
        {
            // FIXED: Use TaskEnvironment.GetCanonicalForm() instead of Path.GetFullPath()
            string canonicalDir = TaskEnvironment.GetCanonicalForm(dir);

            Log.LogMessage(MessageImportance.Low, "Scanning directory: {0}", canonicalDir);

            IEnumerable<string> files = Directory.EnumerateFiles(
                canonicalDir, "*", SearchOption.AllDirectories);

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);

                if (ShouldPreserve(fileName, preservePatterns))
                {
                    Log.LogMessage(MessageImportance.Low,
                        "Preserving file: {0}", filePath);
                    SkippedFiles++;
                    continue;
                }

                bool deleted = TryDeleteFile(filePath);
                if (deleted)
                {
                    CleanedFiles++;
                }
                else
                {
                    SkippedFiles++;
                }
            }

            RemoveEmptyDirectories(canonicalDir);
        }

        private bool TryDeleteFile(string filePath)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    if (!ForceClean)
                    {
                        Log.LogMessage(MessageImportance.Low,
                            "Skipping readonly file: {0}", filePath);
                        return false;
                    }

                    File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                }

                File.Delete(filePath);
                return true;
            }
            catch (IOException) when (HandleLockedFiles)
            {
                Log.LogMessage(MessageImportance.Normal,
                    "File appears locked, checking: {0}", filePath);

                bool isLocked = CheckFileLock(filePath);
                if (isLocked && ForceClean)
                {
                    Log.LogMessage(MessageImportance.Normal,
                        "Attempting force delete: {0}", filePath);
                    return TryForceDelete(filePath);
                }

                if (isLocked)
                {
                    Log.LogWarning("File is locked and ForceClean is disabled: {0}", filePath);
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                Log.LogWarning("Access denied for file: {0}", filePath);
                return false;
            }
        }

        /// <summary>
        /// Checks whether a file is locked by another process using the Sysinternals handle tool.
        /// </summary>
        private bool CheckFileLock(string filePath)
        {
            try
            {
                // FIXED: Use TaskEnvironment.GetProcessStartInfo() instead of creating
                // ProcessStartInfo directly
                var psi = TaskEnvironment.GetProcessStartInfo();
                psi.FileName = "handle.exe";
                psi.Arguments = filePath;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Log.LogMessage(MessageImportance.Low,
                        "Could not start handle.exe to check lock on: {0}", filePath);
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);

                bool locked = output.Contains("pid:", StringComparison.OrdinalIgnoreCase);
                if (locked)
                {
                    Log.LogMessage(MessageImportance.Normal,
                        "Lock detected on {0}: {1}", filePath, output.Trim());
                }

                return locked;
            }
            catch (Exception ex)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Failed to check file lock for {0}: {1}", filePath, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Attempts a force delete via cmd.exe as a last resort for stubborn files.
        /// </summary>
        private bool TryForceDelete(string filePath)
        {
            try
            {
                // FIXED: Use TaskEnvironment.GetProcessStartInfo() instead of creating
                // ProcessStartInfo directly
                var psi = TaskEnvironment.GetProcessStartInfo();
                psi.FileName = "cmd";
                psi.Arguments = $"/c del /f /q \"{filePath}\"";
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Log.LogWarning("Could not start cmd.exe for force delete: {0}", filePath);
                    return false;
                }

                process.WaitForExit(10000);

                if (process.ExitCode == 0 && !File.Exists(filePath))
                {
                    Log.LogMessage(MessageImportance.Normal,
                        "Force deleted: {0}", filePath);
                    return true;
                }

                string stderr = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Log.LogWarning("Force delete failed for {0}: {1}", filePath, stderr.Trim());
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Force delete threw for {0}: {1}", filePath, ex.Message);
                return false;
            }
        }

        private void RemoveEmptyDirectories(string rootDir)
        {
            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        Directory.Delete(subDir, false);
                        Log.LogMessage(MessageImportance.Low,
                            "Removed empty directory: {0}", subDir);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Error removing empty directories under {0}: {1}", rootDir, ex.Message);
            }
        }

        /// <summary>
        /// Determines whether a file name matches any of the preserve patterns.
        /// This is a pure string-matching helper with no thread-safety concerns.
        /// </summary>
        private static bool ShouldPreserve(string fileName, string[] patterns)
        {
            foreach (string pattern in patterns)
            {
                string trimmed = pattern.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (trimmed.StartsWith("*.", StringComparison.Ordinal))
                {
                    string extension = trimmed.Substring(1);
                    if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (fileName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
