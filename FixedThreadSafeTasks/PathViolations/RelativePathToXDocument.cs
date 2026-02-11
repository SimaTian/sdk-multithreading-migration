// FIXED: Resolves relative path through TaskEnvironment before passing to XDocument.Load and Save.
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.PathViolations
{
    [MSBuildMultiThreadableTask]
    public class RelativePathToXDocument : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        public string XmlPath { get; set; } = string.Empty;

        public override bool Execute()
        {
            if (string.IsNullOrEmpty(XmlPath))
            {
                Log.LogError("XmlPath is required.");
                return false;
            }

            string resolvedPath = TaskEnvironment.GetAbsolutePath(XmlPath);

            XDocument doc = XDocument.Load(resolvedPath);

            int elementCount = doc.Descendants().Count();
            Log.LogMessage(MessageImportance.Normal, $"Loaded XML with {elementCount} elements from '{XmlPath}'.");

            doc.Root?.SetAttributeValue("processed", "true");
            doc.Save(resolvedPath);

            Log.LogMessage(MessageImportance.Normal, $"Saved updated XML back to '{XmlPath}'.");
            return true;
        }
    }
}
