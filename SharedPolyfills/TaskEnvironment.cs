using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Build.Framework
{
    public class TaskEnvironment
    {
        private readonly Dictionary<string, string?> _environmentVariables = new(System.StringComparer.OrdinalIgnoreCase);

        public string ProjectDirectory { get; set; } = string.Empty;

        public virtual string GetAbsolutePath(string path)
        {
            if (System.IO.Path.IsPathRooted(path))
                return path;
            return System.IO.Path.Combine(ProjectDirectory, path);
        }

        public virtual string GetCanonicalForm(string path)
        {
            return System.IO.Path.GetFullPath(GetAbsolutePath(path));
        }

        public virtual string? GetEnvironmentVariable(string name)
        {
            // In real MSBuild, this returns the task-scoped env var
            if (_environmentVariables.TryGetValue(name, out var value))
                return value;
            return System.Environment.GetEnvironmentVariable(name);
        }

        public void SetEnvironmentVariable(string name, string? value)
        {
            // In real MSBuild, this sets the task-scoped env var
            _environmentVariables[name] = value;
        }

        public ProcessStartInfo GetProcessStartInfo()
        {
            var psi = new ProcessStartInfo();
            psi.WorkingDirectory = ProjectDirectory;
            return psi;
        }
    }
}
