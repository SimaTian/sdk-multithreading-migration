// FIXED: Environment.CurrentDirectory -> TaskEnvironment.ProjectDirectory,
//        Path.GetFullPath -> TaskEnvironment.GetCanonicalForm,
//        GraphOutputPath resolved via TaskEnvironment.GetAbsolutePath
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.ComplexViolations
{
    [MSBuildMultiThreadableTask]
    public class ProjectDependencyGraphBuilder : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = null!;

        public string SolutionDirectory { get; set; } = string.Empty;

        public string ExcludePatterns { get; set; } = string.Empty;

        public string GraphOutputPath { get; set; } = string.Empty;

        [Output]
        public ITaskItem[] BuildOrder { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public bool HasCircularReferences { get; set; }

        public override bool Execute()
        {
            try
            {
                // FIX: uses TaskEnvironment.ProjectDirectory instead of Environment.CurrentDirectory
                string root = !string.IsNullOrEmpty(SolutionDirectory)
                    ? TaskEnvironment.GetAbsolutePath(SolutionDirectory)
                    : TaskEnvironment.ProjectDirectory;

                Log.LogMessage(MessageImportance.Normal, "Scanning for .csproj files in: {0}", root);
                var excludeDirs = ParseExcludePatterns();
                var projectPaths = DiscoverProjects(root, excludeDirs);
                Log.LogMessage(MessageImportance.Normal, "Discovered {0} project(s).", projectPaths.Count);

                var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string projectPath in projectPaths)
                    graph[projectPath] = ParseProjectReferences(projectPath);

                HasCircularReferences = DetectCycles(graph);
                if (HasCircularReferences)
                    Log.LogWarning("Circular project references detected in the dependency graph.");

                var sorted = TopologicalSort(graph);
                BuildOrder = BuildOutputItems(sorted, graph);

                if (!string.IsNullOrEmpty(GraphOutputPath))
                {
                    // FIX: resolve GraphOutputPath via TaskEnvironment
                    string resolvedGraphPath = TaskEnvironment.GetAbsolutePath(GraphOutputPath);
                    File.WriteAllText(resolvedGraphPath, GenerateDotGraph(graph));
                    Log.LogMessage(MessageImportance.Normal, "DOT graph written to: {0}", resolvedGraphPath);
                }

                Log.LogMessage(MessageImportance.Normal,
                    "Build order resolved: {0} project(s) in topological order.", sorted.Count);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        private HashSet<string> ParseExcludePatterns()
        {
            if (string.IsNullOrEmpty(ExcludePatterns))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(
                ExcludePatterns.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private List<string> DiscoverProjects(string root, HashSet<string> excludeDirs)
        {
            var results = new List<string>();

            foreach (string file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                bool excluded = false;
                foreach (string exclude in excludeDirs)
                {
                    if (file.Contains(Path.DirectorySeparatorChar + exclude + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }

                if (!excluded)
                {
                    // FIX: uses TaskEnvironment.GetCanonicalForm instead of Path.GetFullPath
                    string canonical = TaskEnvironment.GetCanonicalForm(file);
                    results.Add(canonical);
                    Log.LogMessage(MessageImportance.Low, "Found project: {0}", canonical);
                }
            }

            return results;
        }

        private List<string> ParseProjectReferences(string projectPath)
        {
            var references = new List<string>();

            string content = File.ReadAllText(projectPath);
            XDocument doc = XDocument.Parse(content);
            if (doc.Root == null)
                return references;

            var ns = doc.Root.Name.Namespace;
            string projectDir = Path.GetDirectoryName(projectPath) ?? TaskEnvironment.ProjectDirectory;

            foreach (var element in doc.Descendants(ns + "ProjectReference"))
            {
                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(include))
                    continue;

                // FIX: uses TaskEnvironment.GetCanonicalForm instead of Path.GetFullPath
                string combined = Path.Combine(projectDir, include);
                string resolvedRef = TaskEnvironment.GetCanonicalForm(combined);
                references.Add(resolvedRef);
                Log.LogMessage(MessageImportance.Low, "  Reference: {0} -> {1}", include, resolvedRef);
            }

            return references;
        }

        // Pure algorithm — no thread-safety violations
        private bool DetectCycles(Dictionary<string, List<string>> graph)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string node in graph.Keys)
            {
                if (HasCycleDfs(node, graph, visited, inStack))
                    return true;
            }
            return false;
        }

        private bool HasCycleDfs(string node, Dictionary<string, List<string>> graph,
            HashSet<string> visited, HashSet<string> inStack)
        {
            if (inStack.Contains(node)) return true;
            if (visited.Contains(node)) return false;

            visited.Add(node);
            inStack.Add(node);

            if (graph.TryGetValue(node, out var neighbors))
            {
                foreach (string neighbor in neighbors)
                {
                    if (HasCycleDfs(neighbor, graph, visited, inStack))
                        return true;
                }
            }

            inStack.Remove(node);
            return false;
        }

        // Pure algorithm — no thread-safety violations
        private List<string> TopologicalSort(Dictionary<string, List<string>> graph)
        {
            var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string node in graph.Keys)
                inDegree[node] = 0;

            foreach (var kvp in graph)
            {
                foreach (string dep in kvp.Value)
                {
                    if (!inDegree.ContainsKey(dep))
                        inDegree[dep] = 0;
                    inDegree[dep]++;
                }
            }

            var queue = new Queue<string>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0)
                    queue.Enqueue(kvp.Key);
            }

            var result = new List<string>();
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                result.Add(current);

                if (graph.TryGetValue(current, out var deps))
                {
                    foreach (string dep in deps)
                    {
                        inDegree[dep]--;
                        if (inDegree[dep] == 0)
                            queue.Enqueue(dep);
                    }
                }
            }

            return result;
        }

        private string GenerateDotGraph(Dictionary<string, List<string>> graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph ProjectDependencies {");
            sb.AppendLine("    rankdir=BT;");
            sb.AppendLine("    node [shape=box, style=filled, fillcolor=lightblue];");

            foreach (var kvp in graph)
            {
                string fromName = Path.GetFileNameWithoutExtension(kvp.Key);
                foreach (string dep in kvp.Value)
                {
                    string toName = Path.GetFileNameWithoutExtension(dep);
                    sb.AppendLine($"    \"{fromName}\" -> \"{toName}\";");
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private ITaskItem[] BuildOutputItems(List<string> sortedProjects,
            Dictionary<string, List<string>> graph)
        {
            var items = new ITaskItem[sortedProjects.Count];
            for (int i = 0; i < sortedProjects.Count; i++)
            {
                string projectPath = sortedProjects[i];
                var item = new TaskItem(projectPath);
                item.SetMetadata("ProjectName", Path.GetFileNameWithoutExtension(projectPath));
                item.SetMetadata("BuildIndex", i.ToString());
                int depCount = graph.TryGetValue(projectPath, out var deps) ? deps.Count : 0;
                item.SetMetadata("DependencyCount", depCount.ToString());
                items[i] = item;
            }
            return items;
        }
    }
}
