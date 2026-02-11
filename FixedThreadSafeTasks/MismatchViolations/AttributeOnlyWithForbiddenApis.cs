using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.MismatchViolations
{
    [MSBuildMultiThreadableTask]
    public class AttributeOnlyWithForbiddenApis : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string InputPath { get; set; } = string.Empty;

        [Required]
        public string EnvVarName { get; set; } = string.Empty;

        public override bool Execute()
        {
            string resolvedPath = TaskEnvironment.GetAbsolutePath(InputPath);

            if (!File.Exists(resolvedPath))
            {
                Log.LogMessage(MessageImportance.High, $"File not found: {resolvedPath}");
                return true;
            }

            string? envValue = TaskEnvironment.GetEnvironmentVariable(EnvVarName);

            if (!string.IsNullOrEmpty(envValue))
            {
                Log.LogMessage(MessageImportance.Normal,
                    $"Processing '{resolvedPath}' with {EnvVarName}={envValue}");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low,
                    $"Environment variable '{EnvVarName}' is not set; using defaults for '{resolvedPath}'");
            }

            return true;
        }
    }
}
