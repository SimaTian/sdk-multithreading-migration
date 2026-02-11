using Microsoft.Build.Framework;

namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    public static class TaskEnvironmentHelper
    {
        public static TaskEnvironment CreateForTest(string projectDirectory)
        {
            return new TaskEnvironment { ProjectDirectory = projectDirectory };
        }

        public static TaskEnvironment CreateForTest()
        {
            return CreateForTest(Path.GetTempPath());
        }
    }
}
