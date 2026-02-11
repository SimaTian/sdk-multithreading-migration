// FIXED: Returns false and logs error instead of calling Environment.FailFast()
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsEnvironmentFailFast : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string ErrorMessage { get; set; } = string.Empty;

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Checking for critical errors...");

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                Log.LogError($"Critical error detected: {ErrorMessage}");

                // FIXED: Return false instead of calling Environment.FailFast()
                return false;
            }

            Log.LogMessage(MessageImportance.Normal, "No critical errors found.");
            return true;
        }
    }
}
