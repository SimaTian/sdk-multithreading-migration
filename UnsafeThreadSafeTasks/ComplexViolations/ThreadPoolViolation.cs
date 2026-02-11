// VIOLATION: Uses Environment.GetEnvironmentVariable() in a fallback branch inside ProcessWorkItem
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class ThreadPoolViolation : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        [Required]
        public ITaskItem[] WorkItems { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] CompletedItems { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            if (WorkItems.Length == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No work items to process.");
                return true;
            }

            string projectDir = TaskEnvironment.ProjectDirectory;
            Log.LogMessage(MessageImportance.Normal, $"Queuing {WorkItems.Length} work items from '{projectDir}'.");

            var completed = new ConcurrentBag<ITaskItem>();
            var errors = new ConcurrentBag<string>();

            using var countdown = new CountdownEvent(WorkItems.Length);
            using var gate = new ManualResetEventSlim(false);

            foreach (var item in WorkItems)
            {
                var captured = item;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var result = ProcessWorkItem(captured, projectDir);
                        if (result != null)
                            completed.Add(result);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{captured.ItemSpec}: {ex.Message}");
                    }
                    finally
                    {
                        countdown.Signal();
                    }
                });
            }

            if (!countdown.Wait(TimeSpan.FromMinutes(5)))
            {
                Log.LogError("Timed out waiting for work items to complete.");
                return false;
            }

            foreach (string error in errors)
                Log.LogWarning(error);

            CompletedItems = completed.ToArray();
            Log.LogMessage(MessageImportance.Normal,
                $"Completed: {CompletedItems.Length} succeeded, {errors.Count} failed.");
            return errors.IsEmpty;
        }

        private ITaskItem? ProcessWorkItem(ITaskItem item, string projectDir)
        {
            string identity = item.ItemSpec;
            string category = item.GetMetadata("Category") ?? string.Empty;
            string configPath = item.GetMetadata("ConfigPath") ?? string.Empty;

            string resolvedPath;
            if (!string.IsNullOrEmpty(configPath))
            {
                resolvedPath = Path.IsPathRooted(configPath)
                    ? configPath
                    : Path.Combine(projectDir, configPath);
            }
            else if (category.Equals("ConfigPath", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback: locate tooling via environment variable
                // VIOLATION: reads environment variable directly instead of TaskEnvironment.GetEnvironmentVariable
                string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (string.IsNullOrEmpty(dotnetRoot))
                    return null;

                resolvedPath = Path.Combine(dotnetRoot, "sdk", identity);
            }
            else
            {
                resolvedPath = Path.Combine(projectDir, identity);
            }

            var result = new TaskItem(identity);
            result.SetMetadata("ResolvedPath", resolvedPath);
            result.SetMetadata("Category", category);
            result.SetMetadata("Exists", File.Exists(resolvedPath).ToString());
            result.SetMetadata("ProcessedAt", DateTime.UtcNow.ToString("o"));
            return result;
        }
    }
}
