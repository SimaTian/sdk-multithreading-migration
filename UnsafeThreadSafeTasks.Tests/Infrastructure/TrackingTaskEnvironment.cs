using Microsoft.Build.Framework;

namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    /// <summary>
    /// A TaskEnvironment subclass that tracks calls to virtual methods.
    /// Tests can use this to verify that tasks call TaskEnvironment methods
    /// instead of using forbidden APIs like Path.GetFullPath directly.
    /// </summary>
    public class TrackingTaskEnvironment : TaskEnvironment
    {
        public int GetCanonicalFormCallCount { get; private set; }
        public int GetAbsolutePathCallCount { get; private set; }
        public int GetEnvironmentVariableCallCount { get; private set; }
        public List<string> GetCanonicalFormArgs { get; } = new();
        public List<string> GetAbsolutePathArgs { get; } = new();
        public List<string> GetEnvironmentVariableArgs { get; } = new();

        /// <summary>
        /// Records every method call in order, e.g. "GetAbsolutePath(foo.txt)".
        /// </summary>
        public List<string> ApiCalls { get; } = new();

        public bool WasGetAbsolutePathCalled => GetAbsolutePathCallCount > 0;

        public override string GetCanonicalForm(string path)
        {
            GetCanonicalFormCallCount++;
            GetCanonicalFormArgs.Add(path);
            ApiCalls.Add($"GetCanonicalForm({path})");
            return base.GetCanonicalForm(path);
        }

        public override string GetAbsolutePath(string path)
        {
            GetAbsolutePathCallCount++;
            GetAbsolutePathArgs.Add(path);
            ApiCalls.Add($"GetAbsolutePath({path})");
            return base.GetAbsolutePath(path);
        }

        public override string? GetEnvironmentVariable(string name)
        {
            GetEnvironmentVariableCallCount++;
            GetEnvironmentVariableArgs.Add(name);
            ApiCalls.Add($"GetEnvironmentVariable({name})");
            return base.GetEnvironmentVariable(name);
        }
    }
}
