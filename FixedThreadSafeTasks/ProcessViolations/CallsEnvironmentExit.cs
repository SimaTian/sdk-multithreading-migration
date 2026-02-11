// FIXED: Returns false and logs error instead of calling Environment.Exit()
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsEnvironmentExit : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public int ExitCode { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Validating build result...");

            if (ExitCode != 0)
            {
                Log.LogError($"Build failed with exit code {ExitCode}.");

                // FIXED: Return false instead of calling Environment.Exit()
                return false;
            }

            Log.LogMessage(MessageImportance.Normal, "Build validation passed.");
            return true;
        }
    }
}
