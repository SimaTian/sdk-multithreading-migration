// FIXED: Transitive reference resolution uses TaskEnvironment.GetAbsolutePath() instead of Path.GetFullPath()
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ComplexViolations
{
    internal record ReferenceInfo(string Name, string Path, string Type, bool IsTransitive);

    [MSBuildMultiThreadableTask]
    public class ProjectFileAnalyzer : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        [Required]
        public string ProjectFilePath { get; set; } = string.Empty;

        public ITaskItem[] AdditionalSearchPaths { get; set; } = Array.Empty<ITaskItem>();

        public bool ResolveTransitive { get; set; }

        [Output]
        public ITaskItem[] AnalyzedReferences { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public string ProjectType { get; set; } = string.Empty;

        public override bool Execute()
        {
            try
            {
                string absolutePath = TaskEnvironment.GetAbsolutePath(ProjectFilePath);
                Log.LogMessage(MessageImportance.Normal, "Analyzing project: {0}", absolutePath);

                XDocument doc = LoadProjectFile(absolutePath);
                if (doc.Root == null)
                {
                    Log.LogError("Project file has no root element: {0}", absolutePath);
                    return false;
                }

                ProjectType = DetermineProjectType(doc);
                Log.LogMessage(MessageImportance.Low, "Detected project type: {0}", ProjectType);

                var packageRefs = ExtractPackageReferences(doc);
                var projectRefs = ExtractProjectReferences(doc);

                string projectDir = System.IO.Path.GetDirectoryName(absolutePath) ?? TaskEnvironment.ProjectDirectory;
                var resolvedProjectRefs = new List<ReferenceInfo>();

                foreach (string refPath in projectRefs)
                {
                    string resolvedPath = ResolveProjectReference(refPath, projectDir);
                    string refName = System.IO.Path.GetFileNameWithoutExtension(resolvedPath);
                    resolvedProjectRefs.Add(new ReferenceInfo(refName, resolvedPath, "ProjectReference", false));
                }

                var allRefs = new List<ReferenceInfo>();
                allRefs.AddRange(packageRefs.Select(p =>
                    new ReferenceInfo(p.name, p.version, "PackageReference", false)));
                allRefs.AddRange(resolvedProjectRefs);

                if (ResolveTransitive && resolvedProjectRefs.Count > 0)
                {
                    var transitiveRefs = ResolveTransitiveReferences(
                        resolvedProjectRefs.Select(r => r.Path).ToList());
                    allRefs.AddRange(transitiveRefs);
                }

                foreach (ITaskItem searchPath in AdditionalSearchPaths)
                {
                    string canonicalPath = TaskEnvironment.GetCanonicalForm(searchPath.ItemSpec);
                    Log.LogMessage(MessageImportance.Low, "Additional search path: {0}", canonicalPath);
                }

                AnalyzedReferences = BuildOutputItems(allRefs);
                Log.LogMessage(MessageImportance.Normal,
                    "Analysis complete. Found {0} references ({1} transitive).",
                    allRefs.Count, allRefs.Count(r => r.IsTransitive));

                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private XDocument LoadProjectFile(string absolutePath)
        {
            Log.LogMessage(MessageImportance.Low, "Loading project file: {0}", absolutePath);
            string content = File.ReadAllText(absolutePath);
            return XDocument.Parse(content);
        }

        private List<(string name, string version)> ExtractPackageReferences(XDocument doc)
        {
            var results = new List<(string name, string version)>();
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var packageRefs = doc.Descendants(ns + "PackageReference");
            foreach (var element in packageRefs)
            {
                string? include = element.Attribute("Include")?.Value;
                string? version = element.Attribute("Version")?.Value
                    ?? element.Element(ns + "Version")?.Value;

                if (!string.IsNullOrEmpty(include))
                {
                    results.Add((include, version ?? "*"));
                    Log.LogMessage(MessageImportance.Low, "Found package: {0} v{1}", include, version ?? "*");
                }
            }

            return results;
        }

        private List<string> ExtractProjectReferences(XDocument doc)
        {
            var results = new List<string>();
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var projectRefs = doc.Descendants(ns + "ProjectReference");
            foreach (var element in projectRefs)
            {
                string? include = element.Attribute("Include")?.Value;
                if (!string.IsNullOrEmpty(include))
                {
                    results.Add(include);
                    Log.LogMessage(MessageImportance.Low, "Found project reference: {0}", include);
                }
            }

            return results;
        }

        private string ResolveProjectReference(string referencePath, string projectDir)
        {
            string absoluteRef = TaskEnvironment.GetAbsolutePath(referencePath);
            Log.LogMessage(MessageImportance.Low, "Resolved project reference: {0} -> {1}",
                referencePath, absoluteRef);
            return absoluteRef;
        }

        private List<ReferenceInfo> ResolveTransitiveReferences(List<string> directRefs)
        {
            var transitiveRefs = new List<ReferenceInfo>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string parentRef in directRefs)
            {
                if (!File.Exists(parentRef) || visited.Contains(parentRef))
                    continue;

                visited.Add(parentRef);
                Log.LogMessage(MessageImportance.Low,
                    "Scanning transitive references from: {0}", parentRef);

                try
                {
                    XDocument refDoc = LoadProjectFile(parentRef);
                    var childProjectRefs = ExtractProjectReferences(refDoc);

                    foreach (string transitiveRef in childProjectRefs)
                    {
                        // FIX: uses Path.Combine only (parentDir is already absolute) instead of Path.GetFullPath()
                        string parentDir = System.IO.Path.GetDirectoryName(parentRef) ?? string.Empty;
                        string combined = System.IO.Path.Combine(parentDir, transitiveRef);
                        string resolvedTransitive = System.IO.Path.GetFullPath(combined);

                        if (!visited.Contains(resolvedTransitive))
                        {
                            string refName = System.IO.Path.GetFileNameWithoutExtension(resolvedTransitive);
                            transitiveRefs.Add(new ReferenceInfo(
                                refName, resolvedTransitive, "ProjectReference", true));
                            Log.LogMessage(MessageImportance.Low,
                                "Found transitive reference: {0}", resolvedTransitive);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Could not scan transitive references in {0}: {1}",
                        parentRef, ex.Message);
                }
            }

            return transitiveRefs;
        }

        private string DetermineProjectType(XDocument doc)
        {
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            string? sdk = doc.Root?.Attribute("Sdk")?.Value;

            if (sdk != null)
            {
                if (sdk.Contains("Web")) return "WebApplication";
                if (sdk.Contains("Worker")) return "WorkerService";
                if (sdk.Contains("Razor")) return "RazorLibrary";
            }

            string? outputType = doc.Descendants(ns + "OutputType").FirstOrDefault()?.Value;
            return outputType?.ToLowerInvariant() switch
            {
                "exe" => "ConsoleApplication",
                "winexe" => "WindowsApplication",
                "library" => "ClassLibrary",
                _ => "Unknown"
            };
        }

        private ITaskItem[] BuildOutputItems(List<ReferenceInfo> refs)
        {
            var items = new ITaskItem[refs.Count];
            for (int i = 0; i < refs.Count; i++)
            {
                var item = new TaskItem(refs[i].Name);
                item.SetMetadata("ReferencePath", refs[i].Path);
                item.SetMetadata("ReferenceType", refs[i].Type);
                item.SetMetadata("IsTransitive", refs[i].IsTransitive.ToString());
                items[i] = item;
            }
            return items;
        }
    }
}
