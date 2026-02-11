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
        public void BrokenTask_UsesConsoleWriteLine_WritesToConsole()
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

                string consoleOutput = sw.ToString();
                Assert.Contains("Hello from broken task", consoleOutput);
                // Broken task does NOT log to engine
                Assert.Empty(engine.Messages);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void FixedTask_UsesConsoleWriteLine_WritesToBuildEngine()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleWriteLine
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                Message = "Hello from fixed task"
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            var originalOut = Console.Out;
            try
            {
                using var sw = new StringWriter();
                Console.SetOut(sw);

                bool result = task.Execute();

                Assert.True(result);
                // Fixed task should NOT write to Console
                string consoleOutput = sw.ToString();
                Assert.DoesNotContain("Hello from fixed task", consoleOutput);
                // Fixed task logs to build engine
                Assert.Contains(engine.Messages, m => m.Message.Contains("Hello from fixed task"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        // ── UsesConsoleReadLine ─────────────────────────────────────────

        [Fact]
        public void BrokenTask_UsesConsoleReadLine_ReadsFromConsole()
        {
            var engine = new MockBuildEngine();
            var task = new Broken.UsesConsoleReadLine
            {
                BuildEngine = engine
            };

            // The broken task calls Console.ReadLine() — we can verify by providing
            // a StringReader as Console.In and checking it reads from it.
            var originalIn = Console.In;
            try
            {
                Console.SetIn(new StringReader("simulated input"));
                task.Execute();
                Assert.Equal("simulated input", task.UserInput);
            }
            finally
            {
                Console.SetIn(originalIn);
            }
        }

        [Fact]
        public void FixedTask_UsesConsoleReadLine_ReadsFromProperty()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleReadLine
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                DefaultInput = "parameter input"
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            // The fixed task should NOT touch Console.In
            var originalIn = Console.In;
            try
            {
                Console.SetIn(new StringReader("should not be read"));
                bool result = task.Execute();

                Assert.True(result);
                Assert.Equal("parameter input", task.UserInput);
            }
            finally
            {
                Console.SetIn(originalIn);
            }
        }

        // ── UsesConsoleSetOut ───────────────────────────────────────────

        [Fact]
        public void BrokenTask_UsesConsoleSetOut_ChangesConsoleOut()
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

                // After the broken task runs, Console.Out has been changed
                Assert.NotSame(originalOut, Console.Out);
            }
            finally
            {
                // Restore Console.Out and clean up
                Console.Out.Flush();
                Console.Out.Close();
                Console.SetOut(originalOut);
                try { File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void FixedTask_UsesConsoleSetOut_DoesNotChangeConsoleOut()
        {
            var engine = new MockBuildEngine();
            var task = new Fixed.UsesConsoleSetOut
            {
                BuildEngine = engine,
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                LogFilePath = "somefile.log"
            };

            Assert.IsAssignableFrom<IMultiThreadableTask>(task);

            var originalOut = Console.Out;

            bool result = task.Execute();

            Assert.True(result);
            // Console.Out should be unchanged after fixed task runs
            Assert.Same(originalOut, Console.Out);
            // Fixed task logs via build engine instead
            Assert.Contains(engine.Messages, m => m.Message.Contains("Redirected output to log file."));
        }
    }
}
