// FIXED: Logs error and returns false instead of calling Process.GetCurrentProcess().Kill()
using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ProcessViolations
{
    [MSBuildMultiThreadableTask]
    public class CallsProcessKill : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Performing cleanup operations...");

            var currentProcess = Process.GetCurrentProcess();
            Log.LogMessage(MessageImportance.Normal, $"Current process: {currentProcess.ProcessName} (PID: {currentProcess.Id})");

            // FIXED: Log error and return false instead of killing the process
            Log.LogError("Cannot kill the current process from a build task.");
            return false;
        }
    }
}
