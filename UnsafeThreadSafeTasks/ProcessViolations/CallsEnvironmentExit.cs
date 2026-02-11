// VIOLATION: Calls Environment.Exit() which terminates the entire process
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsEnvironmentExit : Microsoft.Build.Utilities.Task
    {
        public int ExitCode { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Validating build result...");

            if (ExitCode != 0)
            {
                Log.LogError($"Build failed with exit code {ExitCode}.");

                // VIOLATION: Must NEVER call Environment.Exit() — it terminates the entire process
                Environment.Exit(ExitCode);
            }

            Log.LogMessage(MessageImportance.Normal, "Build validation passed.");
            return true;
        }
    }
}
