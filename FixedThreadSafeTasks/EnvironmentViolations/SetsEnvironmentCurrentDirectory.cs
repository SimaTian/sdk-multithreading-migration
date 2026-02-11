// FIXED: Removed Environment.CurrentDirectory assignment; uses TaskEnvironment instead
using System;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class SetsEnvironmentCurrentDirectory : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        public string? NewDirectory { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Using project directory: {0}", TaskEnvironment.ProjectDirectory);
            return true;
        }
    }
}
