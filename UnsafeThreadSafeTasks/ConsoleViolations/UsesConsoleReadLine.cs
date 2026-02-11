// VIOLATION: Must never use Console.* — tasks should not interact with console directly
using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleReadLine : Microsoft.Build.Utilities.Task
    {
        [Output]
        public string? UserInput { get; set; }

        public override bool Execute()
        {
            UserInput = Console.ReadLine();
            return true;
        }
    }
}
