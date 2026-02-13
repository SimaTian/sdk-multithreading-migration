using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class ArtifactPublisher : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] SourceDirectories { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string PublishDirectory { get; set; } = string.Empty;

        public string IncludeExtensions { get; set; } = ".dll;.pdb;.xml;.config";

        public int RetryCount { get; set; } = 3;

        [Required]
        public string ManifestPath { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] PublishedFiles { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public int TotalFilesCopied { get; set; }

        public override bool Execute()
        {
            try
            {
                Log.LogMessage(MessageImportance.Normal,
                    "Publishing artifacts from {0} source directories.", SourceDirectories.Length);

                HashSet<string> extensions = ParseExtensions(IncludeExtensions);

                // BUG: uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
                string targetDir = Path.GetFullPath(PublishDirectory);

                // BUG: creates directory using global CWD-resolved path
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    Log.LogMessage(MessageImportance.Low, "Created publish directory: {0}", targetDir);
                }

                var manifest = new List<ManifestEntry>();
                int copiedCount = 0;

                foreach (ITaskItem sourceDir in SourceDirectories)
                {
                    string resolvedSource = ResolveOutputPath(sourceDir.ItemSpec);

                    if (!Directory.Exists(resolvedSource))
                    {
                        Log.LogWarning("Source directory does not exist: {0}", resolvedSource);
                        continue;
                    }

                    string projectName = sourceDir.GetMetadata("ProjectName");
                    if (string.IsNullOrEmpty(projectName))
                        projectName = Path.GetFileName(resolvedSource.TrimEnd(Path.DirectorySeparatorChar));

                    Log.LogMessage(MessageImportance.Low,
                        "Scanning {0} for artifacts (project: {1})...", resolvedSource, projectName);

                    string[] files = Directory.GetFiles(resolvedSource, "*.*", SearchOption.TopDirectoryOnly);

                    foreach (string sourceFile in files)
                    {
                        string ext = Path.GetExtension(sourceFile);
                        if (!extensions.Contains(ext))
                            continue;

                        string fileName = Path.GetFileName(sourceFile);
                        string destPath = Path.Combine(targetDir, fileName);

                        // BUG: uses File.Exists with CWD-relative resolved path
                        if (File.Exists(destPath))
                        {
                            FileInfo srcInfo = new FileInfo(sourceFile);
                            FileInfo destInfo = new FileInfo(destPath);

                            if (srcInfo.LastWriteTimeUtc <= destInfo.LastWriteTimeUtc
                                && srcInfo.Length == destInfo.Length)
                            {
                                Log.LogMessage(MessageImportance.Low,
                                    "Skipping (up-to-date): {0}", fileName);
                                manifest.Add(new ManifestEntry(fileName, destPath, projectName, "skipped"));
                                continue;
                            }
                        }

                        bool success = CopyWithRetry(sourceFile, destPath);
                        if (success)
                        {
                            copiedCount++;
                            manifest.Add(new ManifestEntry(fileName, destPath, projectName, "copied"));
                            Log.LogMessage(MessageImportance.Low,
                                "Copied: {0} -> {1}", sourceFile, destPath);
                        }
                        else
                        {
                            manifest.Add(new ManifestEntry(fileName, destPath, projectName, "failed"));
                            Log.LogWarning("Failed to copy after {0} retries: {1}", RetryCount, fileName);
                        }
                    }
                }

                TotalFilesCopied = copiedCount;
                PublishedFiles = manifest
                    .Where(m => m.Status == "copied" || m.Status == "skipped")
                    .Select(m =>
                    {
                        var item = new TaskItem(m.FileName);
                        item.SetMetadata("DestinationPath", m.DestinationPath);
                        item.SetMetadata("SourceProject", m.SourceProject);
                        item.SetMetadata("Status", m.Status);
                        return (ITaskItem)item;
                    })
                    .ToArray();

                WriteManifest(manifest);

                Log.LogMessage(MessageImportance.Normal,
                    "Publish complete. {0} files copied, {1} total entries in manifest.",
                    copiedCount, manifest.Count);

                return !Log.HasLoggedErrors;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        /// <summary>
        /// Resolves a potentially relative output path to an absolute path.
        /// </summary>
        private string ResolveOutputPath(string dir)
        {
            // BUG: uses Path.GetFullPath which depends on process-global CWD
            return Path.GetFullPath(dir);
        }

        private HashSet<string> ParseExtensions(string extensionList)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(extensionList))
                return result;

            foreach (string ext in extensionList.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = ext.Trim();
                if (!trimmed.StartsWith("."))
                    trimmed = "." + trimmed;
                result.Add(trimmed);
            }

            return result;
        }

        /// <summary>
        /// Copies a file with retry logic for handling locked files.
        /// </summary>
        private bool CopyWithRetry(string source, string destination)
        {
            for (int attempt = 1; attempt <= RetryCount; attempt++)
            {
                try
                {
                    // BUG: uses File.Copy directly with paths resolved via Path.GetFullPath
                    File.Copy(source, destination, overwrite: true);
                    return true;
                }
                catch (IOException ex) when (attempt < RetryCount)
                {
                    Log.LogMessage(MessageImportance.Low,
                        "Copy attempt {0}/{1} failed for {2}: {3}",
                        attempt, RetryCount, Path.GetFileName(source), ex.Message);

                    // BUG: uses Thread.Sleep which blocks the thread in a potentially shared threadpool
                    Thread.Sleep(500 * attempt);
                }
            }

            return false;
        }

        private void WriteManifest(List<ManifestEntry> entries)
        {
            if (string.IsNullOrEmpty(ManifestPath))
                return;

            // BUG: uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
            string manifestFullPath = Path.GetFullPath(ManifestPath);

            string? manifestDir = Path.GetDirectoryName(manifestFullPath);
            if (!string.IsNullOrEmpty(manifestDir) && !Directory.Exists(manifestDir))
                Directory.CreateDirectory(manifestDir);

            var sb = new StringBuilder();
            sb.AppendLine("[");

            for (int i = 0; i < entries.Count; i++)
            {
                ManifestEntry entry = entries[i];
                sb.AppendLine("  {");
                sb.AppendFormat("    \"fileName\": \"{0}\",", EscapeJson(entry.FileName));
                sb.AppendLine();
                sb.AppendFormat("    \"destinationPath\": \"{0}\",", EscapeJson(entry.DestinationPath));
                sb.AppendLine();
                sb.AppendFormat("    \"sourceProject\": \"{0}\",", EscapeJson(entry.SourceProject));
                sb.AppendLine();
                sb.AppendFormat("    \"status\": \"{0}\"", entry.Status);
                sb.AppendLine();
                sb.Append("  }");
                if (i < entries.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("]");

            // BUG: uses File.WriteAllText with path resolved via Path.GetFullPath
            File.WriteAllText(manifestFullPath, sb.ToString(), Encoding.UTF8);

            Log.LogMessage(MessageImportance.Low,
                "Manifest written to {0} ({1} entries).", manifestFullPath, entries.Count);
        }

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private record ManifestEntry(string FileName, string DestinationPath, string SourceProject, string Status);
    }
}
