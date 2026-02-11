using Microsoft.Build.Framework;
using System.Collections;

namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    public class MockBuildEngine : IBuildEngine4
    {
        public List<BuildErrorEventArgs> Errors { get; } = new();
        public List<BuildWarningEventArgs> Warnings { get; } = new();
        public List<BuildMessageEventArgs> Messages { get; } = new();

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.csproj";

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);
        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;

        // IBuildEngine2
        public bool IsRunningMultipleNodes => true;
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs, string toolsVersion) => true;
        public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion) => true;

        // IBuildEngine3
        public BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs) => new BuildEngineResult(true, new List<IDictionary<string, ITaskItem[]>>());
        public void Yield() { }
        public void Reacquire() { }

        // IBuildEngine4
        private readonly Dictionary<object, object> _taskObjects = new();
        public object? GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            _taskObjects.TryGetValue(key, out var value);
            return value;
        }
        public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection)
        {
            _taskObjects[key] = obj;
        }
        public object? UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            _taskObjects.Remove(key, out var value);
            return value;
        }
    }
}
