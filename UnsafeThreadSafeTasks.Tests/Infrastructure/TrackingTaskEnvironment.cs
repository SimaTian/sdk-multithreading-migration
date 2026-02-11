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

        public override string GetCanonicalForm(string path)
        {
            GetCanonicalFormCallCount++;
            GetCanonicalFormArgs.Add(path);
            return base.GetCanonicalForm(path);
        }

        public override string GetAbsolutePath(string path)
        {
            GetAbsolutePathCallCount++;
            GetAbsolutePathArgs.Add(path);
            return base.GetAbsolutePath(path);
        }

        public override string? GetEnvironmentVariable(string name)
        {
            GetEnvironmentVariableCallCount++;
            return base.GetEnvironmentVariable(name);
        }
    }
}
