using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    internal record PackageResult(string Id, string Version, bool IsValid, string? Error);
    internal record PackageSource(string Name, string Url);

    [MSBuildMultiThreadableTask]
    public class NuGetPackageValidator : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string PackagesDirectory { get; set; } = string.Empty;

        [Required]
        public ITaskItem[] PackagesToValidate { get; set; } = Array.Empty<ITaskItem>();

        public bool StrictMode { get; set; }

        public string NuGetConfigPath { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] ValidatedPackages { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] InvalidPackages { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            try
            {
                string resolvedPackagesDir = TaskEnvironment.GetAbsolutePath(PackagesDirectory);
                Log.LogMessage(MessageImportance.Normal,
                    "Validating {0} packages in: {1}", PackagesToValidate.Length, resolvedPackagesDir);

                var packageFolders = new List<string> { resolvedPackagesDir };

                if (!string.IsNullOrEmpty(NuGetConfigPath))
                {
                    string configAbsPath = TaskEnvironment.GetAbsolutePath(NuGetConfigPath);
                    XDocument config = LoadNuGetConfig(configAbsPath);
                    packageFolders.AddRange(GetPackageFolders(config));
                }

                string globalFolder = ReadGlobalPackagesFolder();
                if (!string.IsNullOrEmpty(globalFolder))
                {
                    packageFolders.Add(globalFolder);
                    Log.LogMessage(MessageImportance.Low, "Global packages folder: {0}", globalFolder);
                }

                var results = new List<PackageResult>();
                foreach (ITaskItem package in PackagesToValidate)
                {
                    string packageId = package.ItemSpec;
                    string version = package.GetMetadata("Version") ?? "0.0.0";

                    var result = ValidatePackage(packageId, version, packageFolders);
                    results.Add(result);

                    if (result.IsValid)
                    {
                        Log.LogMessage(MessageImportance.Low,
                            "Package valid: {0} {1}", packageId, version);
                    }
                    else
                    {
                        if (StrictMode)
                            Log.LogError("Package invalid: {0} {1} — {2}", packageId, version, result.Error);
                        else
                            Log.LogWarning("Package invalid: {0} {1} — {2}", packageId, version, result.Error);
                    }
                }

                BuildValidationReport(results);

                Log.LogMessage(MessageImportance.Normal,
                    "Validation complete. {0} valid, {1} invalid.",
                    results.Count(r => r.IsValid), results.Count(r => !r.IsValid));

                return !StrictMode || results.All(r => r.IsValid);
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private XDocument LoadNuGetConfig(string configPath)
        {
            Log.LogMessage(MessageImportance.Low, "Loading NuGet config: {0}", configPath);
            string content = File.ReadAllText(configPath);
            return XDocument.Parse(content);
        }

        private List<string> GetPackageFolders(XDocument config)
        {
            var folders = new List<string>();

            var repositoryPath = config.Descendants("add")
                .Where(e => e.Parent?.Name == "config")
                .FirstOrDefault(e =>
                    string.Equals(e.Attribute("key")?.Value, "repositoryPath",
                        StringComparison.OrdinalIgnoreCase));

            if (repositoryPath != null)
            {
                string rawPath = repositoryPath.Attribute("value")?.Value ?? string.Empty;
                string resolved = TaskEnvironment.GetAbsolutePath(rawPath);
                folders.Add(resolved);
                Log.LogMessage(MessageImportance.Low, "Config repository path: {0}", resolved);
            }

            var fallbackFolders = config.Descendants("add")
                .Where(e => e.Parent?.Name == "fallbackPackageFolders");

            foreach (var folder in fallbackFolders)
            {
                string rawPath = folder.Attribute("value")?.Value ?? string.Empty;
                string resolved = TaskEnvironment.GetAbsolutePath(rawPath);
                folders.Add(resolved);
            }

            return folders;
        }

        private PackageResult ValidatePackage(string packageId, string version, List<string> packageFolders)
        {
            foreach (string folder in packageFolders)
            {
                string? packageDir = FindPackageOnDisk(packageId, version, folder);
                if (packageDir != null)
                {
                    string? contentError = ValidatePackageContents(packageDir);
                    if (contentError == null)
                        return new PackageResult(packageId, version, true, null);
                    else
                        return new PackageResult(packageId, version, false, contentError);
                }
            }

            return new PackageResult(packageId, version, false,
                $"Package {packageId} {version} not found in any configured folder.");
        }

        private string? FindPackageOnDisk(string packageId, string version, string folder)
        {
            string lowerCasePath = Path.Combine(folder, packageId.ToLowerInvariant(), version);
            if (Directory.Exists(lowerCasePath))
                return lowerCasePath;

            string originalCasePath = Path.Combine(folder, packageId, version);
            if (Directory.Exists(originalCasePath))
                return originalCasePath;

            string versionedDir = Path.Combine(folder, $"{packageId}.{version}");
            if (Directory.Exists(versionedDir))
                return versionedDir;

            return null;
        }

        private string? ValidatePackageContents(string packageDir)
        {
            string[] requiredSubDirs = { "lib", "ref", "build", "contentFiles" };
            bool hasAnyContent = false;

            foreach (string subDir in requiredSubDirs)
            {
                string fullPath = Path.Combine(packageDir, subDir);
                if (Directory.Exists(fullPath))
                {
                    hasAnyContent = true;
                    break;
                }
            }

            if (!hasAnyContent)
            {
                string nupkgPattern = "*.nupkg";
                bool hasNupkg = Directory.GetFiles(packageDir, nupkgPattern).Length > 0;
                if (!hasNupkg)
                    return "Package directory has no recognized content structure.";
            }

            string nuspecFile = Directory.GetFiles(packageDir, "*.nuspec").FirstOrDefault() ?? string.Empty;
            if (StrictMode && string.IsNullOrEmpty(nuspecFile))
                return "Strict mode: no .nuspec file found in package.";

            return null;
        }

        private string ReadGlobalPackagesFolder()
        {
            string? envValue = TaskEnvironment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrEmpty(envValue))
            {
                Log.LogMessage(MessageImportance.Low,
                    "Using NUGET_PACKAGES environment variable: {0}", envValue);
                return envValue;
            }

            // BUG: fallback uses Environment.GetFolderPath (process-global) instead of TaskEnvironment
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string fallbackPath = Path.Combine(userProfile, ".nuget", "packages");
            Log.LogMessage(MessageImportance.Low,
                "Falling back to default global packages folder: {0}", fallbackPath);
            return fallbackPath;
        }

        private void BuildValidationReport(List<PackageResult> results)
        {
            var validItems = new List<ITaskItem>();
            var invalidItems = new List<ITaskItem>();

            foreach (var result in results)
            {
                var item = new TaskItem($"{result.Id}/{result.Version}");
                item.SetMetadata("PackageId", result.Id);
                item.SetMetadata("Version", result.Version);
                item.SetMetadata("IsValid", result.IsValid.ToString());

                if (result.IsValid)
                {
                    validItems.Add(item);
                }
                else
                {
                    item.SetMetadata("Error", result.Error ?? "Unknown error");
                    invalidItems.Add(item);
                }
            }

            ValidatedPackages = validItems.ToArray();
            InvalidPackages = invalidItems.ToArray();
        }
    }
}
