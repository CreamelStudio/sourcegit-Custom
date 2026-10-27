using System;
using System.IO;
using System.Linq;
using SourceGit.Models;

internal static class UnityChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.UnityChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "project with spaces & symbols");
            CreateProject(projectPath, "2022.3.62f3");
            var project = UnityProject.Read(projectPath)!;
            Check(project != null && project.EditorVersion == "2022.3.62f3" && project.FullPath == Path.GetFullPath(projectPath), "Read the project's exact Unity version.");
            Check(UnityProject.Find(projectPath, []).Count == 1, "Detect a cloned Unity repository itself.");
            Check(UnityProject.Read(root) == null, "Ordinary repositories must not show the Unity button.");

            var server = Path.Combine(root, "server");
            CreateProject(Path.Combine(server, "Client"), "2022.3.62f3");
            CreateProject(Path.Combine(server, "Mobile"), "6000.3.18f1");
            CreateProject(Path.Combine(root, "outside"), "6000.3.18f1");
            var projects = UnityProject.Find(server, ["Client", "Mobile", "Mobile", "../outside", Path.Combine(root, "outside")]);
            Check(projects.Count == 2 && projects.Select(p => p.EditorVersion).Distinct().Count() == 2, "Detect initialized Client and declared Unity submodules once, excluding paths outside the repository.");

            var incomplete = Path.Combine(root, "incomplete");
            Directory.CreateDirectory(Path.Combine(incomplete, "ProjectSettings"));
            File.WriteAllText(Path.Combine(incomplete, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f3");
            Check(UnityProject.Read(incomplete) == null, "A version file without Assets is not an initialized Unity project.");
            CreateProject(incomplete, "../../unexpected");
            Check(UnityProject.Read(incomplete) == null, "Never use repository-supplied path traversal as an editor version.");
            CreateProject(incomplete, "6000.3.0b12");
            Check(UnityProject.Read(incomplete)?.EditorVersion == "6000.3.0b12", "Recognize a valid beta editor version.");

            var editors = Path.Combine(root, "installed editors");
            Check(project.FindEditor([editors]) == null, "Missing editors must not silently launch another version.");
            var otherEditor = UnityProject.EditorPath(editors, "6000.3.18f1");
            Directory.CreateDirectory(Path.GetDirectoryName(otherEditor)!);
            File.WriteAllText(otherEditor, string.Empty);
            Check(project.FindEditor([editors]) == null, "An installed different editor is not a match.");
            var editor = UnityProject.EditorPath(editors, project.EditorVersion);
            Directory.CreateDirectory(Path.GetDirectoryName(editor)!);
            File.WriteAllText(editor, string.Empty);
            Check(project.FindEditor([editors]) == editor, "Find the exact version in the selected Hub editor folder.");
            var start = project.CreateStartInfo(editor);
            Check(start.FileName == editor && !start.UseShellExecute && start.ArgumentList.SequenceEqual(new[] { "-projectPath", project.FullPath }), "Pass the project path as a single argument without a shell, including spaces and symbols.");
            Check(start.WorkingDirectory == project.FullPath, "Start the editor in the selected AU project.");

            var hubSetting = Path.Combine(root, "secondaryInstallPath.json");
            File.WriteAllText(hubSetting, System.Text.Json.JsonSerializer.Serialize(editors, SourceGit.JsonCodeGen.Default.String));
            Check(UnityProject.ReadHubInstallPath(hubSetting) == editors, "Read Unity Hub's custom editor install folder.");
            File.WriteAllText(hubSetting, "broken json");
            Check(UnityProject.ReadHubInstallPath(hubSetting) == null && UnityProject.ReadHubInstallPath(Path.Combine(root, "missing.json")) == null, "Ignore missing or invalid Hub settings safely.");
            Console.WriteLine("Unity checks passed: root/Client/submodule detection, exact versions, path containment, Hub folders and safe launch arguments. No Unity process started.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CreateProject(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (test)\n");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
