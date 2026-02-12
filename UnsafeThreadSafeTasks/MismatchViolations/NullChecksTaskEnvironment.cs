// VIOLATION: MSBuild always provides TaskEnvironment to IMultiThreadableTask implementations.
// Null-checking and falling back to forbidden APIs is wrong — TaskEnvironment should always
// be used directly without guards. The fallback path uses the unsafe Path.GetFullPath.
using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.MismatchViolations
{
    [MSBuildMultiThreadableTask]
    public class NullChecksTaskEnvironment : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public string InputPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            string resolved;

            // VIOLATION: Null-checks TaskEnvironment (MSBuild always provides it),
            // and uses Path.GetFullPath in both branches — the null check is pointless.
            if (TaskEnvironment != null)
            {
                // Looks like it might use TaskEnvironment but doesn't
                resolved = Path.GetFullPath(InputPath);
            }
            else
            {
                resolved = Path.GetFullPath(InputPath);
            }

            if (File.Exists(resolved))
            {
                long size = new FileInfo(resolved).Length;
                Log.LogMessage(MessageImportance.Normal,
                    $"Resolved input '{InputPath}' to '{resolved}' ({size} bytes)");
            }
            else
            {
                Log.LogMessage(MessageImportance.High,
                    $"Resolved input '{InputPath}' to '{resolved}' but file does not exist");
            }

            return true;
        }
    }
}
