namespace UnsafeThreadSafeTasks.Tests.Infrastructure
{
    public static class TestHelper
    {
        /// <summary>
        /// Creates a unique temp directory that is NOT the process CWD.
        /// Tests should use this as ProjectDirectory to detect tasks that
        /// incorrectly resolve relative to the process CWD instead of ProjectDirectory.
        /// </summary>
        public static string CreateNonCwdTempDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "msbuild-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void CleanupTempDirectory(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
