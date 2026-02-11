// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class LinqPipelineViolation : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        [Required]
        public ITaskItem[] InputItems { get; set; }

        [Output]
        public ITaskItem[] FilteredItems { get; set; }

        public string FilterPattern { get; set; }

        public bool IncludeMetadata { get; set; }

        public string OutputDirectory { get; set; }

        internal record ItemData(string Path, string Category, Dictionary<string, string> Metadata);

        public override bool Execute()
        {
            if (InputItems == null || InputItems.Length == 0)
            {
                FilteredItems = Array.Empty<ITaskItem>();
                return true;
            }

            string resolvedOutputDir = !string.IsNullOrEmpty(OutputDirectory)
                ? TaskEnvironment.GetAbsolutePath(OutputDirectory)
                : TaskEnvironment.ProjectDirectory;

            try
            {
                FilteredItems = InputItems
                    .Where(IsNotExcluded)
                    .Select(ExtractMetadata)
                    .Where(MatchesPattern)
                    .Select(NormalizePaths)
                    .GroupBy(GetCategory)
                    .SelectMany(ResolveGroupPaths)
                    .Where(data => ValidateOutput(data, resolvedOutputDir))
                    .Select(BuildOutputItem)
                    .ToArray();

                Log.LogMessage(MessageImportance.Normal,
                    "Filtered {0} items down to {1} results.", InputItems.Length, FilteredItems.Length);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("Pipeline processing failed: {0}", ex.Message);
                return false;
            }
        }

        private bool IsNotExcluded(ITaskItem item)
        {
            string excluded = item.GetMetadata("Exclude");
            return !string.Equals(excluded, "true", StringComparison.OrdinalIgnoreCase);
        }

        private ItemData ExtractMetadata(ITaskItem item)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (IncludeMetadata)
            {
                foreach (string name in item.MetadataNames.Cast<string>())
                {
                    metadata[name] = item.GetMetadata(name);
                }
            }
            string category = item.GetMetadata("Category") ?? "Default";
            return new ItemData(item.ItemSpec, category, metadata);
        }

        private bool MatchesPattern(ItemData data)
        {
            if (string.IsNullOrEmpty(FilterPattern))
                return true;

            string fileName = System.IO.Path.GetFileName(data.Path);
            return fileName.IndexOf(FilterPattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private ItemData NormalizePaths(ItemData data)
        {
            string normalized = TaskEnvironment.GetCanonicalForm(data.Path);
            return data with { Path = normalized };
        }

        private string GetCategory(ItemData data) => data.Category;

        private IEnumerable<ItemData> ResolveGroupPaths(IGrouping<string, ItemData> group)
        {
            foreach (ItemData item in group)
            {
                if (string.Equals(group.Key, "ExternalReference", StringComparison.OrdinalIgnoreCase))
                {
                    // Violation: uses Path.GetFullPath instead of TaskEnvironment.GetAbsolutePath
                    string resolved = Path.GetFullPath(item.Path);
                    yield return item with { Path = resolved };
                }
                else
                {
                    string resolved = TaskEnvironment.GetAbsolutePath(item.Path);
                    yield return item with { Path = resolved };
                }
            }
        }

        private bool ValidateOutput(ItemData data, string outputDir)
        {
            if (string.IsNullOrEmpty(data.Path))
                return false;

            string extension = System.IO.Path.GetExtension(data.Path);
            return !string.IsNullOrEmpty(extension)
                && !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        private ITaskItem BuildOutputItem(ItemData data)
        {
            var item = new TaskItem(data.Path);
            item.SetMetadata("Category", data.Category);
            item.SetMetadata("ResolvedBy", "LinqPipeline");
            foreach (KeyValuePair<string, string> kvp in data.Metadata)
            {
                item.SetMetadata(kvp.Key, kvp.Value);
            }
            return item;
        }
    }
}
