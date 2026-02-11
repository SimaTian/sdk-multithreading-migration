// VIOLATION: Time-of-check-time-of-use on process-global environment variables.
// Reads an env var, validates it, does work, then reads the same env var again assuming it
// hasn't changed. Another concurrent task can call Environment.SetEnvironmentVariable in between,
// causing this task to silently use a different value than the one it validated.
// FIX: Use TaskEnvironment.GetEnvironmentVariable(ConfigKey) for isolated per-task env vars.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.IntermittentViolations
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
                // VIOLATION: reading from global process environment instead of TaskEnvironment.
                var configValue = Environment.GetEnvironmentVariable(ConfigKey);

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

                // VIOLATION: Re-reading the same global env var — another thread may have
                // called Environment.SetEnvironmentVariable(ConfigKey, ...) by now.
                var finalValue = Environment.GetEnvironmentVariable(ConfigKey);

                // Apply the configuration using the value we just re-read.
                // If another task mutated the global env var, finalValue differs from configValue
                // and we silently use the wrong configuration.
                ResolvedConfig = ApplyConfiguration(finalValue ?? configValue!, resolvedPath);

                if (!string.Equals(configValue, finalValue, StringComparison.Ordinal))
                {
                    // In a single-threaded build this never triggers; under concurrency it can.
                    Log.LogMessage(
                        MessageImportance.Low,
                        "Config value was refreshed from '{0}' to '{1}' during execution.",
                        configValue,
                        finalValue);
                }

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
