// VIOLATION: Must use TaskEnvironment.ProjectDirectory instead of Environment.CurrentDirectory
using System;
using System.IO;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class ReadsEnvironmentCurrentDirectory : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        [Output]
        public string? CurrentDir { get; set; }

        public override bool Execute()
        {
            CurrentDir = Environment.CurrentDirectory;
            string resolvedPath = Path.Combine(CurrentDir, "output");
            Log.LogMessage(MessageImportance.Normal, "Resolved path: {0}", resolvedPath);
            return true;
        }
    }
}
