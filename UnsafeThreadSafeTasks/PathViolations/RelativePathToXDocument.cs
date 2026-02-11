// VIOLATION: XDocument.Load and Save must receive absolute paths. Passes relative path directly to XDocument.Load and doc.Save without resolving through TaskEnvironment.
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.PathViolations
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

            XDocument doc = XDocument.Load(XmlPath);

            int elementCount = doc.Descendants().Count();
            Log.LogMessage(MessageImportance.Normal, $"Loaded XML with {elementCount} elements from '{XmlPath}'.");

            doc.Root?.SetAttributeValue("processed", "true");
            doc.Save(XmlPath);

            Log.LogMessage(MessageImportance.Normal, $"Saved updated XML back to '{XmlPath}'.");
            return true;
        }
    }
}
