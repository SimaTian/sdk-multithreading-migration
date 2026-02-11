// VIOLATION: Must never use Console.SetOut() — it modifies global process state
using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ConsoleViolations
{
    [MSBuildMultiThreadableTask]
    public class UsesConsoleSetOut : Microsoft.Build.Utilities.Task
    {
        public string? LogFilePath { get; set; }

        public override bool Execute()
        {
            Console.SetOut(new StreamWriter(LogFilePath!));
            Console.WriteLine("Redirected output to log file.");
            return true;
        }
    }
}
