// VIOLATION: Calls Process.GetCurrentProcess().Kill() which terminates the entire process
using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsProcessKill : Microsoft.Build.Utilities.Task
    {
        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Performing cleanup operations...");

            var currentProcess = Process.GetCurrentProcess();
            Log.LogMessage(MessageImportance.Normal, $"Current process: {currentProcess.ProcessName} (PID: {currentProcess.Id})");

            // VIOLATION: Must NEVER call Process.GetCurrentProcess().Kill()
            Process.GetCurrentProcess().Kill();

            return true;
        }
    }
}
