// VIOLATION: After resolving via TaskEnvironment, re-resolving with
// Path.GetFullPath is forbidden. Should use TaskEnvironment.GetCanonicalForm()
// if canonicalization is needed.
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.SubtleViolations
{
    [MSBuildMultiThreadableTask]
    public class DoubleResolvesPath : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string InputPath { get; set; } = string.Empty;

        [Output]
        public string? CanonicalPath { get; set; }

        public override bool Execute()
        {
            // First resolution is correct
            var resolved = TaskEnvironment.GetAbsolutePath(InputPath);

            // Second resolution re-invokes Path.GetFullPath, which relies on
            // the process-wide current directory — unsafe in parallel builds.
            var canonical = Path.GetFullPath(resolved);

            CanonicalPath = canonical;

            Log.LogMessage(MessageImportance.Normal,
                $"Input:     {InputPath}");
            Log.LogMessage(MessageImportance.Normal,
                $"Resolved:  {resolved}");
            Log.LogMessage(MessageImportance.Normal,
                $"Canonical: {canonical}");

            return true;
        }
    }
}
