// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.IntermittentViolations
{
    /// <summary>
    /// Resolves framework assemblies for a given target framework by locating the .NET SDK.
    /// The SDK root is lazily read from the DOTNET_ROOT environment variable. In single-threaded
    /// builds the value is stable. In multi-threaded builds another task may call
    /// Environment.SetEnvironmentVariable("DOTNET_ROOT", ...) between construction and first
    /// access, causing this task to see a different SDK than intended.
    /// </summary>
    [MSBuildMultiThreadableTask]
    public class LazyEnvVarCapture : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        /// <summary>
        /// VIOLATION: Lazy captures the global environment variable at first access rather than
        /// reading from TaskEnvironment.GetEnvironmentVariable("DOTNET_ROOT") in Execute().
        /// The Lazy may be initialized on a thread where another task has already overwritten
        /// the global DOTNET_ROOT value for its own purposes.
        /// </summary>
        private readonly Lazy<string> _sdkPath;

        [Required]
        public string TargetFramework { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] FrameworkAssemblies { get; set; } = Array.Empty<ITaskItem>();

        public LazyEnvVarCapture()
        {
            _sdkPath = new Lazy<string>(() =>
                Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? FindSdkFallback());
        }

        public override bool Execute()
        {
            if (string.IsNullOrWhiteSpace(TargetFramework))
            {
                Log.LogError("TargetFramework must be specified.");
                return false;
            }

            string sdkRoot = _sdkPath.Value;
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

        /// <summary>
        /// Probes the shared framework directory structure for a matching TFM.
        /// Returns the directory containing reference assemblies, or null if not found.
        /// </summary>
        private static string? ProbeFrameworkDirectory(string sdkRoot, string tfm)
        {
            // e.g. dotnet/packs/Microsoft.NETCore.App.Ref/8.0.0/ref/net8.0
            string packsDir = Path.Combine(sdkRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packsDir))
                return null;

            // Pick the highest version directory available
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
            item.SetMetadata("FileName", Path.GetFileNameWithoutExtension(assemblyPath));
            item.SetMetadata("Extension", Path.GetExtension(assemblyPath));
            item.SetMetadata("ResolvedFrom", "FrameworkDirectory");
            return item;
        }
    }
}
