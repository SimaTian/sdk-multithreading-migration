using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    internal record LogEntry(string FilePath, string Level, string Code, string Message, int Line);

    [MSBuildMultiThreadableTask]
    public class BuildLogCollector : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public ITaskItem[] LogDirectories { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string OutputReportPath { get; set; } = string.Empty;

        public bool IncludeWarnings { get; set; } = true;

        public string MaxLogAge { get; set; } = string.Empty;

        [Output]
        public int TotalErrors { get; set; }

        [Output]
        public int TotalWarnings { get; set; }

        private static readonly Regex WarningPattern =
            new(@": warning (CS\d+|MSB\d+|NU\d+): (.+)$", RegexOptions.Compiled);

        private static readonly Regex ErrorPattern =
            new(@": error (CS\d+|MSB\d+|NU\d+): (.+)$", RegexOptions.Compiled);

        public override bool Execute()
        {
            try
            {
                // BUG: Path.GetFullPath uses process-global current directory
                string reportPath = Path.GetFullPath(OutputReportPath);
                Log.LogMessage(MessageImportance.Normal,
                    "Collecting build logs. Report: {0}", reportPath);

                TimeSpan? maxAge = ParseMaxAge(MaxLogAge);
                var allEntries = new List<LogEntry>();

                foreach (ITaskItem dirItem in LogDirectories)
                {
                    string dir = dirItem.ItemSpec;
                    // BUG: Directory.GetFiles with relative path depends on current directory
                    string[] logFiles = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories);
                    Log.LogMessage(MessageImportance.Low,
                        "Found {0} log files in: {1}", logFiles.Length, dir);

                    foreach (string logFile in logFiles)
                    {
                        TimeSpan age = GetLogAge(logFile);
                        if (maxAge.HasValue && age > maxAge.Value)
                        {
                            Log.LogMessage(MessageImportance.Low, "Skipping old log: {0}", logFile);
                            continue;
                        }

                        var entries = ParseLogFile(logFile);
                        allEntries.AddRange(entries);
                    }
                }

                TotalErrors = allEntries.Count(e => e.Level == "error");
                TotalWarnings = allEntries.Count(e => e.Level == "warning");

                WriteReportHeader(reportPath);
                WriteReportBody(reportPath, allEntries);
                WriteReportFooter(reportPath);

                Log.LogMessage(MessageImportance.Normal,
                    "Build log report generated. Errors: {0}, Warnings: {1}",
                    TotalErrors, TotalWarnings);

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private List<LogEntry> ParseLogFile(string path)
        {
            var entries = new List<LogEntry>();
            // BUG: File.ReadAllLines + Path.GetFullPath uses process-global current directory
            string[] lines = File.ReadAllLines(Path.GetFullPath(path));

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                Match errorMatch = ErrorPattern.Match(line);
                if (errorMatch.Success)
                {
                    entries.Add(new LogEntry(path, "error",
                        errorMatch.Groups[1].Value, errorMatch.Groups[2].Value, i + 1));
                    continue;
                }

                if (IncludeWarnings)
                {
                    Match warningMatch = WarningPattern.Match(line);
                    if (warningMatch.Success)
                    {
                        entries.Add(new LogEntry(path, "warning",
                            warningMatch.Groups[1].Value, warningMatch.Groups[2].Value, i + 1));
                    }
                }
            }

            return entries;
        }

        private TimeSpan GetLogAge(string filePath)
        {
            // BUG: File.GetLastWriteTime with relative path depends on current directory
            DateTime lastWrite = File.GetLastWriteTime(filePath);
            return DateTime.Now - lastWrite;
        }

        private void WriteReportHeader(string reportPath)
        {
            // BUG: Environment.MachineName is process-global shared state
            string machineName = Environment.MachineName;
            // BUG: Environment.GetEnvironmentVariable is process-global
            string buildNumber = Environment.GetEnvironmentVariable("BUILD_NUMBER") ?? "local";

            string header = $@"<!DOCTYPE html>
<html>
<head><title>Build Log Report — {machineName}</title>
<style>
  body {{ font-family: 'Segoe UI', sans-serif; margin: 20px; }}
  table {{ border-collapse: collapse; width: 100%; }}
  th, td {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
  th {{ background-color: #0078d4; color: white; }}
  .error {{ background-color: #fde7e9; }}
  .warning {{ background-color: #fff4ce; }}
  h1 {{ color: #333; }}
  .summary {{ margin: 15px 0; padding: 10px; background: #f0f0f0; border-radius: 4px; }}
</style></head>
<body>
<h1>Build Log Summary</h1>
<div class=""summary"">
  <strong>Machine:</strong> {machineName} |
  <strong>Build:</strong> {buildNumber} |
  <strong>Generated:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
</div>";

            // BUG: File.WriteAllText with path resolved via Path.GetFullPath (process-global)
            File.WriteAllText(reportPath, header);
        }

        private void WriteReportBody(string reportPath, List<LogEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Level</th><th>Code</th><th>Message</th><th>Source File</th><th>Line</th></tr>");

            var sorted = entries
                .OrderBy(e => e.Level == "error" ? 0 : 1)
                .ThenBy(e => e.Code);

            foreach (var entry in sorted)
            {
                string cssClass = entry.Level == "error" ? "error" : "warning";
                string fileName = Path.GetFileName(entry.FilePath);
                sb.AppendLine($"<tr class=\"{cssClass}\">");
                sb.AppendLine($"  <td>{entry.Level.ToUpperInvariant()}</td>");
                sb.AppendLine($"  <td>{entry.Code}</td>");
                sb.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(entry.Message)}</td>");
                sb.AppendLine($"  <td>{System.Net.WebUtility.HtmlEncode(fileName)}</td>");
                sb.AppendLine($"  <td>{entry.Line}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</table>");

            // BUG: File.AppendAllText builds report incrementally (not thread-safe)
            File.AppendAllText(reportPath, sb.ToString());
        }

        private void WriteReportFooter(string reportPath)
        {
            string footer = @"
</body>
</html>";
            // BUG: File.AppendAllText with process-global resolved path
            File.AppendAllText(reportPath, footer);
        }

        private static TimeSpan? ParseMaxAge(string maxAge)
        {
            if (string.IsNullOrWhiteSpace(maxAge))
                return null;

            string trimmed = maxAge.Trim().ToLowerInvariant();

            if (trimmed.EndsWith("h") &&
                int.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), out int hours))
                return TimeSpan.FromHours(hours);

            if (trimmed.EndsWith("d") &&
                int.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), out int days))
                return TimeSpan.FromDays(days);

            return null;
        }
    }
}
