// FIXED: Uses TaskEnvironment.SetEnvironmentVariable() to avoid modifying global process state
using System;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace FixedThreadSafeTasks.EnvironmentViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesEnvironmentSetVariable : MSBuildTask, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        public string? VariableName { get; set; }

        public string? VariableValue { get; set; }

        public override bool Execute()
        {
            TaskEnvironment.SetEnvironmentVariable(VariableName, VariableValue);
            return true;
        }
    }
}
