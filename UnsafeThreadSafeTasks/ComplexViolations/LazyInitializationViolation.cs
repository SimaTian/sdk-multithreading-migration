// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class LazyInitializationViolation : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        [Required]
        public string ConfigurationFile { get; set; }

        public string TargetFramework { get; set; }

        [Output]
        public ITaskItem[] ResolvedDependencies { get; set; }

        private readonly Lazy<Dictionary<string, string>> _configCache;
        private readonly Lazy<string> _sdkRoot;

        public LazyInitializationViolation()
        {
            _configCache = new Lazy<Dictionary<string, string>>(() =>
            {
                // Violation: Environment.GetEnvironmentVariable used instead of TaskEnvironment.GetEnvironmentVariable
                string nugetPackagesDir = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

                string configPath = Path.Combine(nugetPackagesDir, "cache", "dependency-config.json");
                if (!File.Exists(configPath))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                string content = File.ReadAllText(configPath);
                return ParseConfiguration(content);
            });

            _sdkRoot = new Lazy<string>(() =>
            {
                // Violation: Environment.GetEnvironmentVariable and Path.GetFullPath used directly
                string dotnetRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "");
                return Directory.Exists(dotnetRoot) ? dotnetRoot : string.Empty;
            });
        }

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(ConfigurationFile))
            {
                Log.LogError("ConfigurationFile must be specified.");
                return false;
            }

            string resolvedConfigPath = TaskEnvironment.GetAbsolutePath(ConfigurationFile);
            Dictionary<string, string> config = _configCache.Value;
            string sdkRoot = _sdkRoot.Value;

            string framework = TargetFramework ?? "net8.0";
            var results = new List<ITaskItem>();

            foreach (KeyValuePair<string, string> entry in config)
            {
                string resolvedPath = ResolveDependency(entry.Key, entry.Value, sdkRoot, framework);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    results.Add(BuildOutputItem(entry.Key, resolvedPath));
                }
            }

            ResolvedDependencies = results.ToArray();
            Log.LogMessage(MessageImportance.Normal, "Resolved {0} dependencies for {1}.", results.Count, framework);
            return true;
        }

        private Dictionary<string, string> ParseConfiguration(string content)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#") || !trimmed.Contains("="))
                    continue;

                int separatorIndex = trimmed.IndexOf('=');
                string key = trimmed.Substring(0, separatorIndex).Trim();
                string value = trimmed.Substring(separatorIndex + 1).Trim();
                result[key] = value;
            }
            return result;
        }

        private string ResolveDependency(string name, string version, string sdkRoot, string framework)
        {
            string probePath = TaskEnvironment.GetAbsolutePath(Path.Combine("packages", name, version, "lib", framework));
            if (Directory.Exists(probePath))
                return probePath;

            if (!string.IsNullOrEmpty(sdkRoot))
            {
                string sdkProbePath = Path.Combine(sdkRoot, "packs", name, version);
                if (Directory.Exists(sdkProbePath))
                    return sdkProbePath;
            }

            return null;
        }

        private ITaskItem BuildOutputItem(string name, string resolvedPath)
        {
            var item = new TaskItem(resolvedPath);
            item.SetMetadata("PackageName", name);
            item.SetMetadata("ResolvedFrom", "DependencyCache");
            return item;
        }
    }
}
