// VIOLATION: Calls Environment.FailFast() which terminates the entire process immediately
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsEnvironmentFailFast : Microsoft.Build.Utilities.Task
    {
        public string ErrorMessage { get; set; } = string.Empty;

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Checking for critical errors...");

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                Log.LogError($"Critical error detected: {ErrorMessage}");

                // VIOLATION: Must NEVER call Environment.FailFast() — it terminates the entire process immediately
                Environment.FailFast(ErrorMessage);
            }

            Log.LogMessage(MessageImportance.Normal, "No critical errors found.");
            return true;
        }
    }
}
