// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.IntermittentViolations
{
    /// <summary>
    /// Invokes an external tool and captures its output. Works correctly in single-threaded
    /// builds because MSBuild sets Environment.CurrentDirectory to the project directory before
    /// each task. In multi-threaded builds the process CWD is inherited from the shared
    /// Environment.CurrentDirectory, which may belong to a different project on another thread.
    /// </summary>
    [MSBuildMultiThreadableTask]
    public class ProcessStartInfoInheritsCwd : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        private const int DefaultTimeoutMs = 60_000;

        [Required]
        public string ToolName { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;

        [Output]
        public string ToolOutput { get; set; } = string.Empty;

        public int TimeoutMilliseconds { get; set; } = DefaultTimeoutMs;

        public override bool Execute()
        {
            if (string.IsNullOrWhiteSpace(ToolName))
            {
                Log.LogError("ToolName must be specified.");
                return false;
            }

            Log.LogMessage(MessageImportance.Normal,
                "Launching tool '{0}' with arguments '{1}'.", ToolName, Arguments);

            ProcessStartInfo psi = ConfigureProcess();

            using Process? process = Process.Start(psi);
            if (process == null)
            {
                Log.LogError("Failed to start process '{0}'.", ToolName);
                return false;
            }

            string stdout = ReadProcessOutput(process);
            string stderr = process.StandardError.ReadToEnd();

            bool exited = process.WaitForExit(TimeoutMilliseconds);
            if (!exited)
            {
                Log.LogError("Process '{0}' did not exit within {1} ms.", ToolName, TimeoutMilliseconds);
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Log.LogWarning("stderr: {0}", stderr.Trim());
            }

            ToolOutput = stdout;
            return ValidateExitCode(process.ExitCode);
        }

        /// <summary>
        /// Configures the process start info for tool invocation.
        /// VIOLATION: Creates ProcessStartInfo directly — inherits working directory from the
        /// shared Environment.CurrentDirectory instead of using TaskEnvironment.GetProcessStartInfo()
        /// which would set WorkingDirectory to the project directory.
        /// In single-threaded mode, CWD is correct because MSBuild sets it before each task.
        /// In multi-threaded mode, another thread may change CWD between the set and the launch.
        /// </summary>
        private ProcessStartInfo ConfigureProcess()
        {
            var psi = new ProcessStartInfo(ToolName, Arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // WorkingDirectory is NOT set — inherits from Environment.CurrentDirectory.
                // Fix: use TaskEnvironment.GetProcessStartInfo() and configure FileName/Arguments on it.
            };

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                psi.Environment["PATH"] = pathEnv;
            }

            return psi;
        }

        private string ReadProcessOutput(Process process)
        {
            var sb = new StringBuilder();
            while (!process.StandardOutput.EndOfStream)
            {
                string? line = process.StandardOutput.ReadLine();
                if (line != null)
                {
                    sb.AppendLine(line);
                    Log.LogMessage(MessageImportance.Low, line);
                }
            }
            return sb.ToString().TrimEnd();
        }

        private bool ValidateExitCode(int exitCode)
        {
            if (exitCode == 0)
            {
                Log.LogMessage(MessageImportance.Normal,
                    "Tool '{0}' completed successfully.", ToolName);
                return true;
            }

            Log.LogError("Tool '{0}' exited with code {1}.", ToolName, exitCode);
            return false;
        }
    }
}
