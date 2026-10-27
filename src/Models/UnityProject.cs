using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceGit.Models
{
    public sealed partial class UnityProject
    {
        public string FullPath { get; }
        public string EditorVersion { get; }

        private UnityProject(string fullPath, string editorVersion)
        {
            FullPath = fullPath;
            EditorVersion = editorVersion;
        }

        public static UnityProject Read(string path)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(path, "Assets")))
                    return null;
                foreach (var line in File.ReadLines(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt")))
                {
                    if (!line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                        continue;
                    var version = line[16..].Trim();
                    return version.Length < 64 && VersionPattern().IsMatch(version) ? new UnityProject(Path.GetFullPath(path), version) : null;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return null;
        }

        public static List<UnityProject> Find(string repository, IEnumerable<string> submodules)
        {
            var projects = new List<UnityProject>();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Only inspect known locations; never walk Unity's Library or the whole checkout.
            foreach (var relative in new[] { string.Empty, "Client" }.Concat(submodules))
            {
                if (relative == null || Path.IsPathRooted(relative))
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (!candidate.Equals(root, comparison) && !candidate.StartsWith(prefix, comparison))
                    continue;
                var project = Read(candidate);
                if (project != null && !projects.Any(p => p.FullPath.Equals(candidate, comparison)))
                    projects.Add(project);
            }
            return projects;
        }

        public string FindEditor(IEnumerable<string> roots)
        {
            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var executable = EditorPath(root, EditorVersion);
                if (File.Exists(executable))
                    return executable;
            }
            return null;
        }

        internal static string EditorPath(string root, string version)
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(root, version, "Unity.app", "Contents", "MacOS", "Unity");
            return Path.Combine(root, version, "Editor", OperatingSystem.IsWindows() ? "Unity.exe" : "Unity");
        }

        public ProcessStartInfo CreateStartInfo(string editor)
        {
            var start = new ProcessStartInfo(editor) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = FullPath };
            start.ArgumentList.Add("-projectPath");
            start.ArgumentList.Add(FullPath);
            return start;
        }

        public static IEnumerable<string> EditorRoots()
        {
            var custom = ReadHubInstallPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json"));
            if (!string.IsNullOrWhiteSpace(custom))
                yield return custom;
            if (OperatingSystem.IsWindows())
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
            else if (OperatingSystem.IsMacOS())
                yield return "/Applications/Unity/Hub/Editor";
            else
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor");
        }

        internal static string ReadHubInstallPath(string file)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
            return null;
        }

        [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+(?:c[0-9]+)?$", RegexOptions.CultureInvariant)]
        private static partial Regex VersionPattern();
    }
}
