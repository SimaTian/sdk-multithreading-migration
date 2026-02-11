using System;
using System.IO;
using Microsoft.Build.Framework;
using UnsafeThreadSafeTasks.Tests.Infrastructure;
using Xunit;
using Broken = UnsafeThreadSafeTasks.ConsoleViolations;
using Fixed = FixedThreadSafeTasks.ConsoleViolations;

namespace UnsafeThreadSafeTasks.Tests
{
    public class ConsoleViolationTests
    {
        // ── UsesConsoleWriteLine ─────────────────────────────────────────

        [Fact]
        public void UsesConsoleWriteLine_BrokenTask_ShouldWriteToBuildEngine()
        {
            var engine = new MockBuildEngine();
            var task = new Broken.UsesConsoleWriteLine
            {
                BuildEngine = engine,
                Message = "Hello from broken task"
            };

            var originalOut = Console.Out;
            try
            {
                using var sw = new StringWriter();
                Console.SetOut(sw);

                task.Execute();

                // Assert CORRECT behavior: output should NOT go to Console
                string consoleOutput = sw.ToString();
                Assert.DoesNotContain("Hello from broken task", consoleOutput);
                // Assert CORRECT behavior: output should go to build engine
                Assert.Contains(engine.Messages, m => m.Message!.Contains("Hello from broken task"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void UsesConsoleWriteLine_FixedTask_ShouldWriteToBuildEngine()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleWriteLine
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                Message = "Hello from fixed task"
            };

            var originalOut = Console.Out;
            try
            {
                using var sw = new StringWriter();
                Console.SetOut(sw);

                bool result = task.Execute();

                Assert.True(result);
                // Assert CORRECT behavior: output should NOT go to Console
                string consoleOutput = sw.ToString();
                Assert.DoesNotContain("Hello from fixed task", consoleOutput);
                // Assert CORRECT behavior: output should go to build engine
                Assert.Contains(engine.Messages, m => m.Message!.Contains("Hello from fixed task"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        // ── UsesConsoleReadLine ─────────────────────────────────────────

        [Fact]
        public void UsesConsoleReadLine_BrokenTask_ShouldReadFromProperty()
        {
            var engine = new MockBuildEngine();
            var task = new Broken.UsesConsoleReadLine
            {
                BuildEngine = engine
            };

            var originalIn = Console.In;
            try
            {
                Console.SetIn(new StringReader("should not be read"));

                task.Execute();

                // Assert CORRECT behavior: task should NOT read from Console.In
                // It should use a property instead (like DefaultInput)
                Assert.NotEqual("should not be read", task.UserInput);
            }
            finally
            {
                Console.SetIn(originalIn);
            }
        }

        [Fact]
        public void UsesConsoleReadLine_FixedTask_ShouldReadFromProperty()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleReadLine
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                DefaultInput = "parameter input"
            };

            var originalIn = Console.In;
            try
            {
                Console.SetIn(new StringReader("should not be read"));

                bool result = task.Execute();

                Assert.True(result);
                // Assert CORRECT behavior: task should use DefaultInput, not Console.In
                Assert.Equal("parameter input", task.UserInput);
            }
            finally
            {
                Console.SetIn(originalIn);
            }
        }

        // ── UsesConsoleSetOut ───────────────────────────────────────────

        [Fact]
        public void UsesConsoleSetOut_BrokenTask_ShouldNotChangeConsoleOut()
        {
            var engine = new MockBuildEngine();
            var tempFile = Path.GetTempFileName();
            var task = new Broken.UsesConsoleSetOut
            {
                BuildEngine = engine,
                LogFilePath = tempFile
            };

            var originalOut = Console.Out;
            try
            {
                task.Execute();

                // Assert CORRECT behavior: Console.Out should be unchanged
                Assert.Same(originalOut, Console.Out);
            }
            finally
            {
                if (!ReferenceEquals(originalOut, Console.Out))
                {
                    Console.Out.Flush();
                    Console.Out.Close();
                    Console.SetOut(originalOut);
                }
                try { File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void UsesConsoleSetOut_FixedTask_ShouldNotChangeConsoleOut()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleSetOut
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                LogFilePath = "somefile.log"
            };

            var originalOut = Console.Out;

            bool result = task.Execute();

            Assert.True(result);
            // Assert CORRECT behavior: Console.Out should be unchanged
            Assert.Same(originalOut, Console.Out);
            // Fixed task logs via build engine instead
            Assert.Contains(engine.Messages, m => m.Message!.Contains("Redirected output to log file."));
        }
    }
}
