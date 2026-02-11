// VIOLATION: FileStream constructor must receive absolute path. Passes relative path directly to new FileStream without resolving through TaskEnvironment.
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class RelativePathToFileStream : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string OutputPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(OutputPath))
            {
                Log.LogError("OutputPath is required.");
                return false;
            }

            byte[] data = Encoding.UTF8.GetBytes("Generated output content.");

            using (var stream = new FileStream(OutputPath, FileMode.Create))
            {
                stream.Write(data, 0, data.Length);
            }

            Log.LogMessage(MessageImportance.Normal, $"Wrote {data.Length} bytes to '{OutputPath}'.");
            return true;
        }
    }
}
