// VIOLATION: Must NEVER set Environment.CurrentDirectory as it modifies global process state
using System;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class SetsEnvironmentCurrentDirectory : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        public string? NewDirectory { get; set; }

        public override bool Execute()
        {
            Environment.CurrentDirectory = NewDirectory!;
            Log.LogMessage(MessageImportance.Normal, "Changed working directory to: {0}", NewDirectory);
            return true;
        }
    }
}
