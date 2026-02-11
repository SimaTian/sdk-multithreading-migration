// FIXED: Uses Log.LogMessage() instead of Console.SetOut()
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleSetOut : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string? LogFilePath { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Redirected output to log file.");
            return true;
        }
    }
}
