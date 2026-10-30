using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SourceGit;
using SourceGit.Models;

internal static class ProjectOpenerChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.ProjectOpenerChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = Path.Combine(root, "server & space");
            var worktree = Path.Combine(root, "another worktree");
            Directory.CreateDirectory(Path.Combine(repository, "Server2"));
            Directory.CreateDirectory(Path.Combine(worktree, "Server2"));
            var executable = Path.Combine(root, "program with spaces.exe");
            File.WriteAllText(executable, "not executed");
            var tool = new ExternalTool("User's IDE", executable, null, true);
            var folder = ProjectOpener.FromTool(repository, tool);
            Check(folder.Target == "." && folder.Name == tool.Name, "The default target is the current repository, with no forced IDE fallback.");
            var selected = ProjectOpener.FromTool(repository, tool, Path.Combine(repository, "Server2"));
            Check(selected.Target == "Server2" && selected.ResolveTarget(worktree) == Path.Combine(worktree, "Server2"), "A shared worktree preference resolves against the current worktree, never the original absolute folder.");
            var start = selected.CreateStartInfo(repository);
            Check(start.FileName == executable && !start.UseShellExecute && start.CreateNoWindow && start.WorkingDirectory == repository &&
                start.ArgumentList.SequenceEqual(new[] { Path.Combine(repository, "Server2") }), "Programs and target paths are passed without shell interpolation or argument splitting.");

            foreach (var extension in new[] { ".sln", ".slnx", ".code-workspace" })
            {
                var file = Path.Combine(repository, "project with spaces" + extension);
                File.WriteAllText(file, "fixture");
                var option = new ExternalTool.LaunchOption("Display only", file.Quoted(), file);
                var project = ProjectOpener.FromTool(repository, tool, option.Target);
                Check(project.CreateStartInfo(repository).ArgumentList.Single() == file && project.Target == Path.GetFileName(file), "Preserve a discovered solution/workspace target independently of its display title.");
            }

            var old = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.RepositorySettings)!;
            Check(old.ProjectOpener == null, "Old repositories retain automatic Unity detection or the program chooser.");
            old.ProjectOpener = selected;
            var json = JsonSerializer.Serialize(old, JsonCodeGen.Default.RepositorySettings);
            var restored = JsonSerializer.Deserialize(json, JsonCodeGen.Default.RepositorySettings)!.ProjectOpener!;
            Check(restored.Name == tool.Name && restored.Executable == executable && restored.Target == "Server2" && !restored.IsUnity, "Persist the user's program and relative target through the existing settings JSON.");
            old.ProjectOpener = new ProjectOpener { Name = "Unity", IsUnity = true };
            Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(old, JsonCodeGen.Default.RepositorySettings), JsonCodeGen.Default.RepositorySettings)!.ProjectOpener!.IsUnity,
                "Persist Unity as a version-aware choice, not a fixed editor executable.");

            var gitDir = Path.Combine(repository, ".git");
            Directory.CreateDirectory(gitDir);
            var viewModel = new SourceGit.ViewModels.Repository(false, repository, gitDir);
            viewModel.SetProjectOpener(selected);
            var settingsFile = Path.Combine(gitDir, "sourcegit.settings");
            Check(File.Exists(settingsFile) && JsonSerializer.Deserialize(File.ReadAllText(settingsFile), JsonCodeGen.Default.RepositorySettings)!.ProjectOpener!.Target == "Server2",
                "The first user selection is saved immediately to this repository's existing settings file.");
            Check(viewModel.HasGenericProjectOpenerIcon && !viewModel.UsesUnityProjectOpener, "A non-Unity repository with a custom program uses a generic icon, never Unity.");
            viewModel.SetProjectOpener(null);
            Check(JsonSerializer.Deserialize(File.ReadAllText(settingsFile), JsonCodeGen.Default.RepositorySettings)!.ProjectOpener == null,
                "Resetting the association is persisted too.");

            ExpectFailure(() => ProjectOpener.RelativeTarget(repository, worktree), "Do not attach targets outside this repository.");
            foreach (var invalid in new[] { "../another worktree", Path.Combine(worktree, "Server2"), "missing folder", "" })
                ExpectFailure(() => new ProjectOpener { Executable = executable, Target = invalid }.CreateStartInfo(repository), "Reject escaping, absolute and missing targets.");
            ExpectFailure(() => new ProjectOpener { Executable = "relative.exe" }.CreateStartInfo(repository), "Do not resolve a configured executable from PATH or the repository.");
            File.Delete(executable);
            ExpectFailure(() => selected.CreateStartInfo(repository), "A removed IDE must request a new selection, never silently run another program.");
            ExpectFailure(() => new ProjectOpener { IsUnity = true }.CreateStartInfo(repository), "A server with no root Unity project cannot launch Unity.");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "SourceGit.ProjectOpenerChecks") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new Exception("Unexpected test cleanup path.");
            Directory.Delete(root, true);
        }
        Console.WriteLine("Project opener checks passed: user selection/persistence, folder/solution/workspace targets, worktree-relative paths, safe arguments, missing programs and Unity exclusion. No IDE process started.");
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
