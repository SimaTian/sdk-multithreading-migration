// FIXED: All helper methods now use TaskEnvironment for path resolution and environment
// variable access, matching the correct usage in Execute(). No more Path.GetFullPath() or
// Environment.GetEnvironmentVariable() calls bypassing the task-scoped environment.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.MismatchViolations
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
                    string absolutePath = TaskEnvironment.GetAbsolutePath(projectFile.ItemSpec);

                    if (!File.Exists(absolutePath))
                    {
                        Log.LogWarning("Project file not found: {0}", absolutePath);
                        continue;
                    }

                    if (CreateBackups)
                    {
                        CreateVersionBackup(absolutePath);
                    }

                    string content = ReadProjectFile(absolutePath);
                    bool isCsproj = absolutePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

                    string patched = isCsproj
                        ? PatchCsprojContent(content, fullVersion, assemblyVersion)
                        : PatchAssemblyInfoContent(content, fullVersion, assemblyVersion);

                    if (patched != content)
                    {
                        WriteProjectFile(absolutePath, patched);

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

        // FIX: Uses TaskEnvironment.GetAbsolutePath() for all path resolution
        private void CreateVersionBackup(string resolvedFilePath)
        {
            string backupDir = string.IsNullOrEmpty(BackupDirectory)
                ? Path.GetDirectoryName(resolvedFilePath) ?? string.Empty
                : TaskEnvironment.GetAbsolutePath(BackupDirectory);

            string fileName = Path.GetFileName(resolvedFilePath);
            string backupPath = Path.Combine(backupDir, fileName + ".bak");

            File.Copy(resolvedFilePath, backupPath, overwrite: true);

            Log.LogMessage(MessageImportance.Low, "Created backup: {0}", backupPath);
        }

        // FIX: Caller now passes already-resolved absolute path
        private string ReadProjectFile(string absolutePath)
        {
            return File.ReadAllText(absolutePath);
        }

        // FIX: Caller now passes already-resolved absolute path
        private void WriteProjectFile(string absolutePath, string content)
        {
            File.WriteAllText(absolutePath, content);
        }

        // FIX: Uses TaskEnvironment.GetEnvironmentVariable() instead of Environment directly
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
                buildNum = TaskEnvironment.GetEnvironmentVariable("BUILD_BUILDNUMBER") ?? string.Empty;
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
