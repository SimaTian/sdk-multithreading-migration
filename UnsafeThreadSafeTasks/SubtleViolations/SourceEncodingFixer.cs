// VIOLATION: All Path.GetFullPath / Path.GetTempFileName calls are hidden inside
// private helper methods, making them easy to miss during code review.
// Every path resolution must go through TaskEnvironment, even in helpers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class SourceEncodingFixer : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string SourceDirectory { get; set; } = string.Empty;

        public string FileExtensions { get; set; } = ".cs;.xml;.json";

        public string TargetEncoding { get; set; } = "utf-8-bom";

        public bool NormalizeLineEndings { get; set; } = true;

        public bool CreateBackups { get; set; } = true;

        [Output]
        public ITaskItem[] ModifiedFiles { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public string BackupDirectory { get; set; } = string.Empty;

        public override bool Execute()
        {
            try
            {
                var extensions = ParseExtensions(FileExtensions);
                var targetEnc = ResolveEncoding(TargetEncoding);
                var sourceFiles = CollectSourceFiles(extensions);

                if (sourceFiles.Count == 0)
                {
                    Log.LogMessage(MessageImportance.Normal, "No source files found to process.");
                    return true;
                }

                Log.LogMessage(MessageImportance.Normal,
                    $"Scanning {sourceFiles.Count} files for encoding issues...");

                if (CreateBackups)
                {
                    EnsureBackupDirectory();
                }

                var modified = new List<ITaskItem>();

                foreach (var file in sourceFiles)
                {
                    var currentEnc = DetectEncoding(file);
                    bool needsFix = !EncodingsMatch(currentEnc, targetEnc);
                    bool needsLineEndingFix = NormalizeLineEndings && HasMixedLineEndings(file);

                    if (needsFix || needsLineEndingFix)
                    {
                        if (CreateBackups)
                        {
                            CreateBackupCopy(file);
                        }

                        NormalizeFile(file, targetEnc, needsLineEndingFix);

                        var item = new TaskItem(file);
                        item.SetMetadata("OriginalEncoding", currentEnc.EncodingName);
                        item.SetMetadata("NewEncoding", targetEnc.EncodingName);
                        item.SetMetadata("LineEndingsNormalized", needsLineEndingFix.ToString());
                        modified.Add(item);

                        Log.LogMessage(MessageImportance.Normal,
                            $"Fixed: {file} ({currentEnc.EncodingName} -> {targetEnc.EncodingName})");
                    }
                }

                ModifiedFiles = modified.ToArray();
                Log.LogMessage(MessageImportance.High,
                    $"Encoding fix complete. {ModifiedFiles.Length} of {sourceFiles.Count} files modified.");

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private HashSet<string> ParseExtensions(string extensions)
        {
            return new HashSet<string>(
                extensions.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(e => e.StartsWith(".") ? e : "." + e),
                StringComparer.OrdinalIgnoreCase);
        }

        private Encoding ResolveEncoding(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                "utf-16" => Encoding.Unicode,
                "ascii" => Encoding.ASCII,
                _ => Encoding.GetEncoding(name),
            };
        }

        // VIOLATION: Path.GetFullPath depends on process-wide current directory
        private List<string> CollectSourceFiles(HashSet<string> extensions)
        {
            var rootDir = Path.GetFullPath(SourceDirectory);
            return Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => extensions.Contains(Path.GetExtension(f)))
                            .ToList();
        }

        // VIOLATION: Hidden two levels deep — File.ReadAllBytes with Path.GetFullPath
        private Encoding DetectEncoding(string filePath)
        {
            var resolvedPath = ResolvePath(filePath);
            var bytes = File.ReadAllBytes(resolvedPath);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode;

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        private bool HasMixedLineEndings(string filePath)
        {
            var resolved = ResolvePath(filePath);
            var content = File.ReadAllText(resolved);
            bool hasCrLf = content.Contains("\r\n");
            var withoutCrLf = content.Replace("\r\n", "");
            bool hasBareLf = withoutCrLf.Contains("\n");
            bool hasBareCr = withoutCrLf.Contains("\r");
            return (hasCrLf && hasBareLf) || (hasCrLf && hasBareCr) || (hasBareLf && hasBareCr);
        }

        // VIOLATION: Uses Path.GetTempFileName (process-wide temp dir) and File.Move
        // with paths resolved via ResolvePath which calls Path.GetFullPath
        private void NormalizeFile(string filePath, Encoding targetEncoding, bool fixLineEndings)
        {
            var resolved = ResolvePath(filePath);
            var content = File.ReadAllText(resolved);

            if (fixLineEndings)
            {
                content = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            }

            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, content, targetEncoding);
            File.Delete(resolved);
            File.Move(tempFile, resolved);
        }

        // VIOLATION: Backup directory resolved via Path.GetFullPath
        private void EnsureBackupDirectory()
        {
            var rootDir = Path.GetFullPath(SourceDirectory);
            BackupDirectory = Path.Combine(rootDir, ".encoding-backups",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(BackupDirectory);
        }

        // VIOLATION: File.Copy with destination resolved through Path.GetFullPath
        private void CreateBackupCopy(string originalFile)
        {
            var resolvedOriginal = ResolvePath(originalFile);
            var rootDir = Path.GetFullPath(SourceDirectory);
            var relativePath = Path.GetRelativePath(rootDir, resolvedOriginal);
            var backupPath = Path.Combine(Path.GetFullPath(BackupDirectory), relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(resolvedOriginal, backupPath, overwrite: true);
        }

        // VIOLATION: Hidden helper — Path.GetFullPath depends on process-wide current directory
        private string ResolvePath(string path)
        {
            return Path.GetFullPath(path);
        }

        private static bool EncodingsMatch(Encoding a, Encoding b)
        {
            return string.Equals(a.WebName, b.WebName, StringComparison.OrdinalIgnoreCase)
                && Equals(a.GetPreamble().Length > 0, b.GetPreamble().Length > 0);
        }
    }
}
