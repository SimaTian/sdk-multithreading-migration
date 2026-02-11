using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.SubtleViolations
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
            var resolvedPrimary = TaskEnvironment.GetAbsolutePath(PrimaryPath);

            var resolvedSecondary = TaskEnvironment.GetAbsolutePath(SecondaryPath);

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
