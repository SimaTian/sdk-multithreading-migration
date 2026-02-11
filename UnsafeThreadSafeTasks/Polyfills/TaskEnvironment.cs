using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Build.Framework
{
    public class TaskEnvironment
    {
        public string ProjectDirectory { get; set; } = string.Empty;

        public string GetAbsolutePath(string path)
        {
            if (System.IO.Path.IsPathRooted(path))
                return path;
            return System.IO.Path.Combine(ProjectDirectory, path);
        }

        public string GetCanonicalForm(string path)
        {
            return System.IO.Path.GetFullPath(GetAbsolutePath(path));
        }

        public string? GetEnvironmentVariable(string name)
        {
            // In real MSBuild, this returns the task-scoped env var
            return System.Environment.GetEnvironmentVariable(name);
        }

        public void SetEnvironmentVariable(string name, string? value)
        {
            // In real MSBuild, this sets the task-scoped env var
        }

        public ProcessStartInfo GetProcessStartInfo()
        {
            var psi = new ProcessStartInfo();
            psi.WorkingDirectory = ProjectDirectory;
            return psi;
        }
    }
}
