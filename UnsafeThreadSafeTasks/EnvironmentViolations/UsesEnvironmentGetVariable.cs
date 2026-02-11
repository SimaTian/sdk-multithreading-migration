// VIOLATION: Must use TaskEnvironment.GetEnvironmentVariable() for thread-safe env var access
using System;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesEnvironmentGetVariable : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        public string? VariableName { get; set; }

        [Output]
        public string? VariableValue { get; set; }

        public override bool Execute()
        {
            VariableValue = Environment.GetEnvironmentVariable(VariableName!);
            return true;
        }
    }
}
