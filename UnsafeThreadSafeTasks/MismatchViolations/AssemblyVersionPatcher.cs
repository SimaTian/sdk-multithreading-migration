// VIOLATION: Execute() correctly uses TaskEnvironment for path resolution, but the helper
// methods it calls bypass TaskEnvironment entirely. This "mismatch" pattern is hard to catch
// because the top-level method looks correct — the bugs hide in the private helpers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.MismatchViolations
{
    [MSBuildMultiThreadableTask]
    public class AssemblyVersionPatcher : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] ProjectFiles { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string VersionPrefix { get; set; } = string.Empty;

        public string VersionSuffix { get; set; } = string.Empty;

        public string BuildNumber { get; set; } = string.Empty;

        public bool CreateBackups { get; set; } = true;

        public string BackupDirectory { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] PatchedFiles { get; set; } = Array.Empty<ITaskItem>();

        private static readonly Regex CsprojVersionRegex = new(
            @"<Version>([^<]*)</Version>",
            RegexOptions.Compiled);
        private static readonly Regex CsprojAssemblyVersionRegex = new(
            @"<AssemblyVersion>([^<]*)</AssemblyVersion>",
            RegexOptions.Compiled);
        private static readonly Regex CsprojFileVersionRegex = new(
            @"<FileVersion>([^<]*)</FileVersion>",
            RegexOptions.Compiled);
        private static readonly Regex AssemblyInfoVersionRegex = new(
            @"\[assembly:\s*AssemblyVersion\(""([^""]*)""\)\]",
            RegexOptions.Compiled);
        private static readonly Regex AssemblyInfoFileVersionRegex = new(
            @"\[assembly:\s*AssemblyFileVersion\(""([^""]*)""\)\]",
            RegexOptions.Compiled);
        private static readonly Regex AssemblyInfoInformationalRegex = new(
            @"\[assembly:\s*AssemblyInformationalVersion\(""([^""]*)""\)\]",
            RegexOptions.Compiled);

        public override bool Execute()
        {
            try
            {
                string fullVersion = BuildVersionString();
                string assemblyVersion = BuildAssemblyVersion();

                Log.LogMessage(MessageImportance.High,
                    "Patching version: {0} (AssemblyVersion: {1})", fullVersion, assemblyVersion);

                var patchedItems = new List<ITaskItem>();

                foreach (ITaskItem projectFile in ProjectFiles)
                {
                    // CORRECT: Execute() uses TaskEnvironment for path resolution
                    string absolutePath = TaskEnvironment.GetAbsolutePath(projectFile.ItemSpec);

                    if (!File.Exists(absolutePath))
                    {
                        Log.LogWarning("Project file not found: {0}", absolutePath);
                        continue;
                    }

                    if (CreateBackups)
                    {
                        CreateVersionBackup(projectFile.ItemSpec);
                    }

                    // BUG: passes raw ItemSpec to ReadProjectFile which doesn't resolve it
                    string content = ReadProjectFile(projectFile.ItemSpec);
                    bool isCsproj = absolutePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

                    string patched = isCsproj
                        ? PatchCsprojContent(content, fullVersion, assemblyVersion)
                        : PatchAssemblyInfoContent(content, fullVersion, assemblyVersion);

                    if (patched != content)
                    {
                        WriteProjectFile(projectFile.ItemSpec, patched);

                        var item = new TaskItem(absolutePath);
                        item.SetMetadata("OriginalVersion", ExtractCurrentVersion(content, isCsproj));
                        item.SetMetadata("NewVersion", fullVersion);
                        item.SetMetadata("FileType", isCsproj ? "csproj" : "AssemblyInfo");
                        patchedItems.Add(item);

                        Log.LogMessage(MessageImportance.Normal, "Patched: {0}", absolutePath);
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.Low,
                            "No version tokens found in: {0}", absolutePath);
                    }
                }

                PatchedFiles = patchedItems.ToArray();
                Log.LogMessage(MessageImportance.High,
                    "Version patching complete. {0} of {1} files modified.",
                    patchedItems.Count, ProjectFiles.Length);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        // BUG: Uses Path.GetFullPath() instead of TaskEnvironment.GetAbsolutePath()
        private void CreateVersionBackup(string filePath)
        {
            string backupDir = string.IsNullOrEmpty(BackupDirectory)
                ? Path.GetDirectoryName(filePath) ?? string.Empty
                : BackupDirectory;

            string fileName = Path.GetFileName(filePath);
            string backupPath = Path.Combine(backupDir, fileName + ".bak");

            File.Copy(Path.GetFullPath(filePath), Path.GetFullPath(backupPath), overwrite: true);

            Log.LogMessage(MessageImportance.Low, "Created backup: {0}", backupPath);
        }

        // BUG: Reads file using unresolved path directly — path comes from TaskItem
        private string ReadProjectFile(string path)
        {
            return File.ReadAllText(path);
        }

        // BUG: Uses Path.GetFullPath() instead of TaskEnvironment.GetAbsolutePath()
        private void WriteProjectFile(string path, string content)
        {
            File.WriteAllText(Path.GetFullPath(path), content);
        }

        // BUG: Falls back to Environment.GetEnvironmentVariable() directly
        private string BuildVersionString()
        {
            string version = VersionPrefix;

            if (!string.IsNullOrEmpty(VersionSuffix))
            {
                version = $"{version}-{VersionSuffix}";
            }

            string buildNum = BuildNumber;
            if (string.IsNullOrEmpty(buildNum))
            {
                buildNum = Environment.GetEnvironmentVariable("BUILD_BUILDNUMBER") ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(buildNum))
            {
                version = string.IsNullOrEmpty(VersionSuffix)
                    ? $"{version}+{buildNum}"
                    : $"{version}.{buildNum}";
            }

            return version;
        }

        private string BuildAssemblyVersion()
        {
            string[] parts = VersionPrefix.Split('.');
            return parts.Length >= 2
                ? $"{parts[0]}.{parts[1]}.0.0"
                : $"{VersionPrefix}.0.0.0";
        }

        private string PatchCsprojContent(string content, string fullVersion, string assemblyVersion)
        {
            content = CsprojVersionRegex.Replace(content,
                $"<Version>{fullVersion}</Version>");
            content = CsprojAssemblyVersionRegex.Replace(content,
                $"<AssemblyVersion>{assemblyVersion}</AssemblyVersion>");
            content = CsprojFileVersionRegex.Replace(content,
                $"<FileVersion>{fullVersion}</FileVersion>");
            return content;
        }

        private string PatchAssemblyInfoContent(string content, string fullVersion, string assemblyVersion)
        {
            content = AssemblyInfoVersionRegex.Replace(content,
                $"[assembly: AssemblyVersion(\"{assemblyVersion}\")]");
            content = AssemblyInfoFileVersionRegex.Replace(content,
                $"[assembly: AssemblyFileVersion(\"{fullVersion}\")]");
            content = AssemblyInfoInformationalRegex.Replace(content,
                $"[assembly: AssemblyInformationalVersion(\"{fullVersion}\")]");
            return content;
        }

        private string ExtractCurrentVersion(string content, bool isCsproj)
        {
            Regex regex = isCsproj ? CsprojVersionRegex : AssemblyInfoVersionRegex;
            Match match = regex.Match(content);
            return match.Success ? match.Groups[1].Value : "unknown";
        }
    }
}
