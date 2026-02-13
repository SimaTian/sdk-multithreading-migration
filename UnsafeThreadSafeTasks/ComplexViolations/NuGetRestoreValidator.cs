using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class NuGetRestoreValidator : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] PackageReferences { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string PackagesDirectory { get; set; } = string.Empty;

        [Required]
        public string TargetFramework { get; set; } = string.Empty;

        [Output]
        public string ValidationReport { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] MissingPackages { get; set; } = Array.Empty<ITaskItem>();

        private static readonly Dictionary<string, string[]> TfmCompatibilityMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "net9.0", new[] { "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" } },
            { "net8.0", new[] { "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" } },
            { "net7.0", new[] { "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" } },
            { "net6.0", new[] { "net6.0", "netstandard2.1", "netstandard2.0" } },
            { "netstandard2.1", new[] { "netstandard2.1", "netstandard2.0", "netstandard1.6" } },
            { "netstandard2.0", new[] { "netstandard2.0", "netstandard1.6", "netstandard1.0" } },
        };

        public override bool Execute()
        {
            try
            {
                // BUG: Uses Path.GetFullPath (process-global CWD) instead of TaskEnvironment.GetAbsolutePath
                string resolvedPackagesDir = Path.GetFullPath(PackagesDirectory);

                if (!Directory.Exists(resolvedPackagesDir))
                {
                    resolvedPackagesDir = ResolveDefaultPackagesDirectory();
                }

                Log.LogMessage(MessageImportance.Normal,
                    "Validating {0} package references in: {1}", PackageReferences.Length, resolvedPackagesDir);

                var reportLines = new List<string>();
                var missingItems = new List<ITaskItem>();
                int validCount = 0;

                reportLines.Add($"NuGet Restore Validation Report — {DateTime.UtcNow:u}");
                reportLines.Add($"Target Framework: {TargetFramework}");
                reportLines.Add($"Packages Directory: {resolvedPackagesDir}");
                reportLines.Add(new string('-', 72));

                foreach (ITaskItem packageRef in PackageReferences)
                {
                    string packageId = packageRef.ItemSpec;
                    string version = packageRef.GetMetadata("Version") ?? "0.0.0";

                    var result = ValidateSinglePackage(packageId, version);

                    if (result.IsPresent)
                    {
                        string assemblyStatus = result.HasAssembly ? "assembly found" : "no assembly (meta-package?)";
                        reportLines.Add($"  OK   {packageId}/{version} — {assemblyStatus}");

                        if (!string.IsNullOrEmpty(result.NuspecVersion) &&
                            !VersionsMatch(version, result.NuspecVersion))
                        {
                            reportLines.Add($"  WARN {packageId}: requested {version} but nuspec has {result.NuspecVersion}");
                            Log.LogWarning("Version mismatch for {0}: requested {1}, found {2}",
                                packageId, version, result.NuspecVersion);
                        }

                        validCount++;
                    }
                    else
                    {
                        reportLines.Add($"  MISS {packageId}/{version} — {result.Error}");

                        var item = new TaskItem(packageId);
                        item.SetMetadata("Version", version);
                        item.SetMetadata("Error", result.Error ?? "Not found on disk");
                        missingItems.Add(item);

                        Log.LogError("Missing package: {0} {1} — {2}", packageId, version, result.Error);
                    }
                }

                reportLines.Add(new string('-', 72));
                reportLines.Add($"Summary: {validCount} valid, {missingItems.Count} missing out of {PackageReferences.Length} total.");

                MissingPackages = missingItems.ToArray();
                WriteValidationReport(reportLines);

                Log.LogMessage(MessageImportance.Normal,
                    "Restore validation complete. {0} valid, {1} missing.", validCount, missingItems.Count);

                return missingItems.Count == 0;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private string ResolveDefaultPackagesDirectory()
        {
            // BUG: Uses Environment.GetEnvironmentVariable (process-global) instead of TaskEnvironment
            string? nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrEmpty(nugetPackages))
            {
                Log.LogMessage(MessageImportance.Low, "Using NUGET_PACKAGES env: {0}", nugetPackages);
                return nugetPackages;
            }

            // BUG: Uses Environment.GetFolderPath (process-global) instead of TaskEnvironment
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultPath = Path.Combine(userProfile, ".nuget", "packages");
            Log.LogMessage(MessageImportance.Low, "Falling back to default: {0}", defaultPath);
            return defaultPath;
        }

        private PackageValidationResult ValidateSinglePackage(string id, string version)
        {
            // BUG: Uses Path.GetFullPath (process-global CWD) instead of TaskEnvironment.GetAbsolutePath
            string packageDir = Path.GetFullPath(
                Path.Combine(PackagesDirectory, id.ToLowerInvariant(), version));

            if (!Directory.Exists(packageDir))
            {
                string altPath = Path.Combine(PackagesDirectory, id, version);
                // BUG: Again uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
                packageDir = Path.GetFullPath(altPath);

                if (!Directory.Exists(packageDir))
                    return new PackageValidationResult(false, false, null,
                        $"Package directory not found: {id}/{version}");
            }

            string? nuspecVersion = ReadNuspecVersion(packageDir, id);

            bool hasAssembly = FindPackageAssembly(packageDir, TargetFramework) != null;

            return new PackageValidationResult(true, hasAssembly, nuspecVersion, null);
        }

        private string? FindPackageAssembly(string packageDir, string tfm)
        {
            string libDir = Path.Combine(packageDir, "lib");
            if (!Directory.Exists(libDir))
                return null;

            string[] compatibleTfms = GetCompatibleFrameworks(tfm);

            foreach (string compatTfm in compatibleTfms)
            {
                string tfmDir = Path.Combine(libDir, compatTfm);
                if (!Directory.Exists(tfmDir))
                    continue;

                foreach (string assembly in Directory.EnumerateFiles(tfmDir, "*.dll"))
                {
                    Log.LogMessage(MessageImportance.Low, "Found assembly: {0}", assembly);
                    return assembly;
                }
            }

            string refDir = Path.Combine(packageDir, "ref");
            if (Directory.Exists(refDir))
            {
                foreach (string compatTfm in compatibleTfms)
                {
                    string refTfmDir = Path.Combine(refDir, compatTfm);
                    if (!Directory.Exists(refTfmDir))
                        continue;

                    foreach (string assembly in Directory.EnumerateFiles(refTfmDir, "*.dll"))
                    {
                        Log.LogMessage(MessageImportance.Low, "Found ref assembly: {0}", assembly);
                        return assembly;
                    }
                }
            }

            return null;
        }

        private string? ReadNuspecVersion(string packageDir, string packageId)
        {
            try
            {
                string nuspecPath = Path.Combine(packageDir, $"{packageId}.nuspec");
                if (!File.Exists(nuspecPath))
                {
                    nuspecPath = Path.Combine(packageDir, $"{packageId.ToLowerInvariant()}.nuspec");
                    if (!File.Exists(nuspecPath))
                        return null;
                }

                // Uses File.ReadAllText to read .nuspec files
                string content = File.ReadAllText(nuspecPath);
                XDocument nuspec = XDocument.Parse(content);

                XNamespace ns = nuspec.Root?.Name.Namespace ?? XNamespace.None;
                string? version = nuspec.Descendants(ns + "version").FirstOrDefault()?.Value;
                return version;
            }
            catch (Exception ex)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Failed to read nuspec for {0}: {1}", packageId, ex.Message);
                return null;
            }
        }

        private static string[] GetCompatibleFrameworks(string tfm)
        {
            if (TfmCompatibilityMap.TryGetValue(tfm, out string[]? compatList))
                return compatList;

            return new[] { tfm, "netstandard2.0" };
        }

        private static bool VersionsMatch(string requested, string actual)
        {
            if (string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Version.TryParse(requested, out Version? reqVer) &&
                Version.TryParse(actual, out Version? actVer))
            {
                return reqVer.Major == actVer.Major &&
                       reqVer.Minor == actVer.Minor &&
                       reqVer.Build == actVer.Build;
            }

            return false;
        }

        private void WriteValidationReport(List<string> lines)
        {
            string reportDir = Path.Combine(
                TaskEnvironment.ProjectDirectory, "obj", TargetFramework);
            string reportPath = Path.Combine(reportDir, "nuget-restore-validation.txt");

            try
            {
                if (!Directory.Exists(reportDir))
                    Directory.CreateDirectory(reportDir);

                // Uses File.WriteAllText for the validation report
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines));
                ValidationReport = reportPath;
                Log.LogMessage(MessageImportance.Normal, "Validation report: {0}", reportPath);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not write validation report: {0}", ex.Message);
                ValidationReport = string.Empty;
            }
        }

        private record PackageValidationResult(
            bool IsPresent,
            bool HasAssembly,
            string? NuspecVersion,
            string? Error);
    }
}
