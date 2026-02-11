// FIXED: Uses Log.LogMessage() instead of Console.WriteLine()
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleWriteLine : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string? Message { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, Message);
            return true;
        }
    }
}
