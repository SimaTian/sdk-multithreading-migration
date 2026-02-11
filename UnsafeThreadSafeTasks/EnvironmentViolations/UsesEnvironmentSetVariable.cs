// VIOLATION: Must use TaskEnvironment.SetEnvironmentVariable() to avoid modifying global process state
using System;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace UnsafeThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesEnvironmentSetVariable : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        public string? VariableName { get; set; }

        public string? VariableValue { get; set; }

        public override bool Execute()
        {
            Environment.SetEnvironmentVariable(VariableName!, VariableValue);
            return true;
        }
    }
}
