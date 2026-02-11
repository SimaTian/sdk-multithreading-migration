// VIOLATION: Must never use Console.* — use MSBuild logging APIs (Log.LogMessage) instead
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleWriteLine : Microsoft.Build.Utilities.Task
    {
        public string? Message { get; set; }

        public override bool Execute()
        {
            Console.WriteLine(Message);
            return true;
        }
    }
}
