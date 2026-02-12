using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    internal record AssemblyInfo(string Name, string Version, string Culture, string PublicKeyToken);

    [MSBuildMultiThreadableTask]
    public class AssemblyReferenceResolver : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string TargetFramework { get; set; } = string.Empty;

        public string RuntimeIdentifier { get; set; } = string.Empty;

        public ITaskItem[] FrameworkDirectories { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] ResolvedReferences { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] UnresolvedReferences { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            try
            {
                Log.LogMessage(MessageImportance.Normal,
                    "Resolving {0} references for {1}/{2}",
                    References.Length, TargetFramework, RuntimeIdentifier);

                List<string> searchPaths = BuildSearchPaths(TargetFramework, RuntimeIdentifier);
                Log.LogMessage(MessageImportance.Low,
                    "Built {0} search paths for probing.", searchPaths.Count);

                var resolutions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                foreach (ITaskItem reference in References)
                {
                    string assemblyName = reference.ItemSpec;
                    string? hintPath = reference.GetMetadata("HintPath");

                    if (!string.IsNullOrEmpty(hintPath))
                    {
                        string absoluteHint = TaskEnvironment.GetAbsolutePath(hintPath);
                        if (File.Exists(absoluteHint))
                        {
                            resolutions[assemblyName] = absoluteHint;
                            Log.LogMessage(MessageImportance.Low,
                                "Resolved via HintPath: {0} -> {1}", assemblyName, absoluteHint);
                            continue;
                        }
                    }

                    string? resolved = ResolveReference(assemblyName, searchPaths);
                    if (resolved != null)
                    {
                        resolutions[assemblyName] = resolved;
                        Log.LogMessage(MessageImportance.Low,
                            "Resolved: {0} -> {1}", assemblyName, resolved);
                    }
                    else
                    {
                        string? externalResult = RunExternalResolver(assemblyName);
                        resolutions[assemblyName] = externalResult;

                        if (externalResult != null)
                            Log.LogMessage(MessageImportance.Low,
                                "Resolved via external: {0} -> {1}", assemblyName, externalResult);
                        else
                            Log.LogWarning("Could not resolve assembly: {0}", assemblyName);
                    }
                }

                CollectResults(resolutions);

                int resolvedCount = resolutions.Count(kv => kv.Value != null);
                int unresolvedCount = resolutions.Count(kv => kv.Value == null);
                Log.LogMessage(MessageImportance.Normal,
                    "Resolution complete. {0} resolved, {1} unresolved.",
                    resolvedCount, unresolvedCount);

                return unresolvedCount == 0;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private List<string> BuildSearchPaths(string targetFx, string rid)
        {
            var paths = new List<string>();

            string projectDir = TaskEnvironment.ProjectDirectory;
            paths.Add(Path.Combine(projectDir, "bin"));
            paths.Add(Path.Combine(projectDir, "obj"));

            foreach (ITaskItem fxDir in FrameworkDirectories)
            {
                string absoluteFxDir = TaskEnvironment.GetAbsolutePath(fxDir.ItemSpec);
                paths.Add(absoluteFxDir);
                Log.LogMessage(MessageImportance.Low, "Framework dir: {0}", absoluteFxDir);
            }

            string refPackPath = TaskEnvironment.GetAbsolutePath(
                Path.Combine("packs", $"Microsoft.NETCore.App.Ref", targetFx, "ref"));
            paths.Add(refPackPath);

            // BUG: reads DOTNET_ROOT from global Environment instead of TaskEnvironment
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                string runtimePackPath = Path.Combine(dotnetRoot, "packs",
                    "Microsoft.NETCore.App.Runtime." + rid, targetFx, "runtimes", rid, "lib", targetFx);
                // BUG: also uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
                string resolvedRuntimePack = Path.GetFullPath(runtimePackPath);
                paths.Add(resolvedRuntimePack);
                Log.LogMessage(MessageImportance.Low, "Runtime pack: {0}", resolvedRuntimePack);
            }

            string? fxRefPath = TaskEnvironment.GetEnvironmentVariable("FRAMEWORK_REFERENCE_PATH");
            if (!string.IsNullOrEmpty(fxRefPath))
            {
                paths.Add(TaskEnvironment.GetAbsolutePath(fxRefPath));
            }

            return paths;
        }

        private string? ResolveReference(string assemblyName, List<string> searchPaths)
        {
            foreach (string searchPath in searchPaths)
            {
                string? found = ProbeDirectory(searchPath, assemblyName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private string? ProbeDirectory(string directory, string assemblyName)
        {
            if (!Directory.Exists(directory))
                return null;

            string dllPath = Path.Combine(directory, assemblyName + ".dll");
            if (File.Exists(dllPath))
                return dllPath;

            string exePath = Path.Combine(directory, assemblyName + ".exe");
            if (File.Exists(exePath))
                return exePath;

            string subdirDll = Path.Combine(directory, assemblyName, assemblyName + ".dll");
            if (File.Exists(subdirDll))
                return subdirDll;

            return null;
        }

        private AssemblyInfo? GetAssemblyMetadata(string assemblyPath)
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                byte[]? publicKeyToken = assemblyName.GetPublicKeyToken();
                string tokenStr = publicKeyToken != null && publicKeyToken.Length > 0
                    ? BitConverter.ToString(publicKeyToken).Replace("-", "").ToLowerInvariant()
                    : "null";

                return new AssemblyInfo(
                    assemblyName.Name ?? Path.GetFileNameWithoutExtension(assemblyPath),
                    assemblyName.Version?.ToString() ?? "0.0.0.0",
                    assemblyName.CultureName ?? "neutral",
                    tokenStr);
            }
            catch (Exception ex)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Could not read metadata from {0}: {1}", assemblyPath, ex.Message);
                return null;
            }
        }

        private string? RunExternalResolver(string assemblyName)
        {
            try
            {
                // BUG: creates ProcessStartInfo directly instead of using TaskEnvironment.GetProcessStartInfo()
                var psi = new ProcessStartInfo("dotnet", $"resolve {assemblyName}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return null;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(15000);

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && File.Exists(output))
                    return output;

                return null;
            }
            catch (Exception ex)
            {
                Log.LogMessage(MessageImportance.Low,
                    "External resolver failed for {0}: {1}", assemblyName, ex.Message);
                return null;
            }
        }

        private void CollectResults(Dictionary<string, string?> resolutions)
        {
            var resolved = new List<ITaskItem>();
            var unresolved = new List<ITaskItem>();

            foreach (var kvp in resolutions)
            {
                if (kvp.Value != null)
                {
                    var item = new TaskItem(kvp.Key);
                    item.SetMetadata("ResolvedPath", kvp.Value);

                    AssemblyInfo? metadata = GetAssemblyMetadata(kvp.Value);
                    if (metadata != null)
                    {
                        item.SetMetadata("Version", metadata.Version);
                        item.SetMetadata("Culture", metadata.Culture);
                        item.SetMetadata("PublicKeyToken", metadata.PublicKeyToken);
                    }

                    resolved.Add(item);
                }
                else
                {
                    var item = new TaskItem(kvp.Key);
                    item.SetMetadata("TargetFramework", TargetFramework);
                    item.SetMetadata("RuntimeIdentifier", RuntimeIdentifier);
                    unresolved.Add(item);
                }
            }

            ResolvedReferences = resolved.ToArray();
            UnresolvedReferences = unresolved.ToArray();
        }
    }
}
