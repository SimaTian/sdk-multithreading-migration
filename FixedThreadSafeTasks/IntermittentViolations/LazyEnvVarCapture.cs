// FIXED: Reads DOTNET_ROOT from TaskEnvironment.GetEnvironmentVariable() in Execute(),
// not via Lazy that captures global env var.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class LazyEnvVarCapture : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string TargetFramework { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] FrameworkAssemblies { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (string.IsNullOrWhiteSpace(TargetFramework))
            {
                Log.LogError("TargetFramework must be specified.");
                return false;
            }

            // FIX: Read from TaskEnvironment in Execute() instead of Lazy with global env var
            string sdkRoot = TaskEnvironment.GetEnvironmentVariable("DOTNET_ROOT") ?? FindSdkFallback();
            if (string.IsNullOrEmpty(sdkRoot) || !Directory.Exists(sdkRoot))
            {
                Log.LogError("Could not locate the .NET SDK. Set the DOTNET_ROOT environment variable.");
                return false;
            }

            Log.LogMessage(MessageImportance.Normal,
                "Using SDK root '{0}' for framework '{1}'.", sdkRoot, TargetFramework);

            string? frameworkDir = ProbeFrameworkDirectory(sdkRoot, TargetFramework);
            if (frameworkDir == null)
            {
                Log.LogWarning("No framework assemblies found for '{0}' under '{1}'.",
                    TargetFramework, sdkRoot);
                FrameworkAssemblies = Array.Empty<ITaskItem>();
                return true;
            }

            var items = new List<ITaskItem>();
            foreach (string assemblyPath in Directory.EnumerateFiles(frameworkDir, "*.dll"))
            {
                items.Add(BuildAssemblyItem(assemblyPath));
            }

            FrameworkAssemblies = items.ToArray();
            Log.LogMessage(MessageImportance.Normal,
                "Resolved {0} framework assemblies for '{1}'.", items.Count, TargetFramework);
            return true;
        }

        private static string FindSdkFallback()
        {
            // Common default install locations
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                "/usr/share/dotnet",
                "/usr/local/share/dotnet",
            };

            return candidates.FirstOrDefault(Directory.Exists) ?? string.Empty;
        }

        private static string? ProbeFrameworkDirectory(string sdkRoot, string tfm)
        {
            string packsDir = Path.Combine(sdkRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packsDir))
                return null;

            string? versionDir = Directory.EnumerateDirectories(packsDir)
                .OrderByDescending(d => Path.GetFileName(d))
                .FirstOrDefault();

            if (versionDir == null)
                return null;

            string refDir = Path.Combine(versionDir, "ref", tfm);
            return Directory.Exists(refDir) ? refDir : null;
        }

        private static ITaskItem BuildAssemblyItem(string assemblyPath)
        {
            var item = new TaskItem(assemblyPath);
            item.SetMetadata("AssemblyFileName", Path.GetFileNameWithoutExtension(assemblyPath));
            item.SetMetadata("FileExtension", Path.GetExtension(assemblyPath));
            item.SetMetadata("ResolvedFrom", "FrameworkDirectory");
            return item;
        }
    }
}
