// FIXED: Uses TaskEnvironment.GetEnvironmentVariable() and reads once, caching the value.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class EnvVarToctou : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string ConfigKey { get; set; } = string.Empty;

        public string FallbackValue { get; set; } = string.Empty;

        [Output]
        public string ResolvedConfig { get; set; } = string.Empty;

        public override bool Execute()
        {
            try
            {
                // FIX: Use TaskEnvironment.GetEnvironmentVariable instead of Environment.GetEnvironmentVariable
                var configValue = TaskEnvironment.GetEnvironmentVariable(ConfigKey);

                if (!ValidateConfig(configValue))
                {
                    Log.LogMessage(
                        MessageImportance.Normal,
                        "Config key '{0}' is not set or empty; using fallback '{1}'.",
                        ConfigKey,
                        FallbackValue);
                    configValue = FallbackValue;
                }

                Log.LogMessage(
                    MessageImportance.Normal,
                    "Initial config value for '{0}': '{1}'",
                    ConfigKey,
                    configValue);

                var resolvedPath = ResolveConfigPath(configValue!);
                var configHash = ComputeChecksum(configValue!);

                Log.LogMessage(
                    MessageImportance.Low,
                    "Config path resolved to '{0}', hash: {1}",
                    resolvedPath,
                    configHash);

                // FIX: Reuse the cached configValue instead of re-reading the env var
                ResolvedConfig = ApplyConfiguration(configValue!, resolvedPath);

                Log.LogMessage(
                    MessageImportance.Normal,
                    "Resolved configuration for '{0}': '{1}'",
                    ConfigKey,
                    ResolvedConfig);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private bool ValidateConfig(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Reject values that contain invalid path characters as a basic sanity check.
            foreach (var ch in Path.GetInvalidPathChars())
            {
                if (value.Contains(ch))
                {
                    Log.LogWarning("Config value for '{0}' contains invalid character U+{1:X4}.", ConfigKey, (int)ch);
                    return false;
                }
            }

            return true;
        }

        private string ResolveConfigPath(string configValue)
        {
            // Build a path from the config value relative to the project directory.
            var candidate = Path.Combine(TaskEnvironment.ProjectDirectory, configValue);

            if (Directory.Exists(candidate))
            {
                Log.LogMessage(MessageImportance.Low, "Config directory exists: '{0}'", candidate);
                return candidate;
            }

            var parentDir = Path.GetDirectoryName(candidate);
            if (parentDir != null && Directory.Exists(parentDir))
            {
                Log.LogMessage(MessageImportance.Low, "Parent directory exists: '{0}'", parentDir);
                return candidate;
            }

            // Return the raw value when we can't resolve it to an existing location.
            return configValue;
        }

        private string ApplyConfiguration(string value, string resolvedPath)
        {
            // Combine the resolved path context with the value to produce a final config string.
            var combined = $"{resolvedPath}|{value}";
            var hash = ComputeChecksum(combined);

            return $"{value} (context={Path.GetFileName(resolvedPath)}, integrity={hash})";
        }

        private static string ComputeChecksum(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes)[..12];
        }
    }
}
