// FIXED: Uses a task parameter instead of Console.ReadLine()
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleReadLine : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string? DefaultInput { get; set; }

        [Output]
        public string? UserInput { get; set; }

        public override bool Execute()
        {
            UserInput = DefaultInput;
            return true;
        }
    }
}
