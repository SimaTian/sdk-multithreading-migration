// VIOLATION: Implements IMultiThreadableTask (has the TaskEnvironment property) but
// completely ignores TaskEnvironment in all code paths. Uses Path.GetFullPath and
// Environment.GetEnvironmentVariable directly instead of the provided TaskEnvironment.
using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.MismatchViolations
{
    [MSBuildMultiThreadableTask]
    public class IgnoresTaskEnvironment : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string InputPath { get; set; } = string.Empty;

        [Required]
        public string EnvVarName { get; set; } = string.Empty;

        public override bool Execute()
        {
            string resolvedPath = Path.GetFullPath(InputPath);

            string? configValue = Environment.GetEnvironmentVariable(EnvVarName);

            if (string.IsNullOrEmpty(configValue))
            {
                Log.LogMessage(MessageImportance.Low,
                    $"Environment variable '{EnvVarName}' not set; skipping configuration override");
                configValue = "default";
            }

            if (Directory.Exists(resolvedPath))
            {
                string[] files = Directory.GetFiles(resolvedPath, "*.*", SearchOption.TopDirectoryOnly);
                Log.LogMessage(MessageImportance.Normal,
                    $"Found {files.Length} file(s) in '{resolvedPath}' with config '{configValue}'");
            }
            else if (File.Exists(resolvedPath))
            {
                Log.LogMessage(MessageImportance.Normal,
                    $"Processing file '{resolvedPath}' with config '{configValue}'");
            }
            else
            {
                Log.LogMessage(MessageImportance.High,
                    $"Path '{resolvedPath}' does not exist");
            }

            return true;
        }
    }
}
