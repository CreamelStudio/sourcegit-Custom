using System;
using System.Diagnostics;
using System.IO;

namespace SourceGit.Models
{
    public sealed class ProjectOpener
    {
        public string Name { get; set; } = string.Empty;
        public string Executable { get; set; } = string.Empty;
        public string Target { get; set; } = ".";
        public bool IsUnity { get; set; }

        public static ProjectOpener FromTool(string repository, ExternalTool tool, string target = null)
        {
            var result = new ProjectOpener { Name = tool.Name, Executable = tool.ExecFile };
            result.Target = RelativeTarget(repository, target ?? repository);
            return result;
        }

        public static string RelativeTarget(string repository, string target)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(repository), Path.GetFullPath(target));
            ResolveTarget(repository, relative);
            return relative;
        }

        public string ResolveTarget(string repository) => ResolveTarget(repository, Target);

        public ProcessStartInfo CreateStartInfo(string repository)
        {
            if (IsUnity)
            {
                var project = UnityProject.Read(repository) ?? throw new DirectoryNotFoundException("Unity project not found.");
                var editor = project.FindEditor(UnityProject.EditorRoots()) ?? throw new FileNotFoundException($"Unity {project.EditorVersion} was not found.");
                return project.CreateStartInfo(editor);
            }

            if (string.IsNullOrWhiteSpace(Executable) || !Path.IsPathFullyQualified(Executable) || !File.Exists(Executable))
                throw new FileNotFoundException("The selected program is no longer available.", Executable);

            var start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetFullPath(repository),
            };
            start.ArgumentList.Add(ResolveTarget(repository));
            return start;
        }

        private static string ResolveTarget(string repository, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new ArgumentException("Choose a target inside this repository.");

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
            var target = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!target.Equals(root, comparison) && !target.StartsWith(prefix, comparison))
                throw new ArgumentException("Choose a target inside this repository.");
            if (!Directory.Exists(target) && !File.Exists(target))
                throw new FileNotFoundException("The selected project target is no longer available.", target);
            return target;
        }
    }
}
