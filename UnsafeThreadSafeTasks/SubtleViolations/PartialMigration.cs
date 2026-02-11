// VIOLATION: Partial migration — PrimaryPath is correctly resolved through
// TaskEnvironment, but SecondaryPath still uses Path.GetFullPath.
// ALL paths must be resolved through TaskEnvironment.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class PartialMigration : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string PrimaryPath { get; set; } = string.Empty;

        [Required]
        public string SecondaryPath { get; set; } = string.Empty;

        [Output]
        public bool FilesMatch { get; set; }

        public override bool Execute()
        {
            // Correctly resolved through TaskEnvironment
            var resolvedPrimary = TaskEnvironment.GetAbsolutePath(PrimaryPath);

            // BUG: Still uses Path.GetFullPath — depends on process-wide current directory
            var resolvedSecondary = Path.GetFullPath(SecondaryPath);

            Log.LogMessage(MessageImportance.Normal,
                $"Primary:   {resolvedPrimary}");
            Log.LogMessage(MessageImportance.Normal,
                $"Secondary: {resolvedSecondary}");

            FilesMatch = File.Exists(resolvedPrimary)
                      && File.Exists(resolvedSecondary)
                      && string.Equals(
                             File.ReadAllText(resolvedPrimary),
                             File.ReadAllText(resolvedSecondary));

            Log.LogMessage(MessageImportance.Normal,
                $"Files match: {FilesMatch}");

            return true;
        }
    }
}
