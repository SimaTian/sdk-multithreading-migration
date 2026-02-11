// FIXED: Uses TaskEnvironment.GetProcessStartInfo() which sets WorkingDirectory.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.IntermittentViolations
{
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

        private ProcessStartInfo ConfigureProcess()
        {
            // FIX: Use TaskEnvironment.GetProcessStartInfo() to get a PSI with WorkingDirectory set
            var psi = TaskEnvironment.GetProcessStartInfo();
            psi.FileName = ToolName;
            psi.Arguments = Arguments;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

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
