using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SourceGit;
using SourceGit.Models;

internal static class SubmoduleDiscardChecks
{
    public static async Task RunAsync()
    {
        var originalGit = SourceGit.Native.OS.GitExecutable;
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.SubmoduleDiscardChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SourceGit.Native.OS.GitExecutable = "git";
            var defaults = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.RepositorySettings);
            Check(defaults.LockSubmoduleDiscard, "Existing repositories must default to locked submodule discard.");

            var repository = Path.Combine(root, "parent");
            var source = Path.Combine(root, "source");
            await Init(repository);
            await Init(source);
            const string modulePath = "Client space 한글";
            await Git(repository, "-c", "protocol.file.allow=always", "submodule", "add", source, modulePath);
            await Git(repository, "commit", "-am", "Add submodule");
            var initialHead = await Git(repository, "rev-parse", "HEAD");
            await Git(repository, "branch", "base", initialHead);

            var settings = RepositorySettings.Get(Path.Combine(repository, ".git"));
            settings.LockSubmoduleDiscard = false;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(repository, ".git", "sourcegit.settings")), JsonCodeGen.Default.RepositorySettings);
            Check(!saved.LockSubmoduleDiscard, "Unlocking must be saved immediately for this repository.");
            settings.LockSubmoduleDiscard = true;

            var unborn = Path.Combine(root, "unborn");
            await Init(unborn);
            var unbornBase = await Git(unborn, "rev-parse", "HEAD");
            await Git(unborn, "update-ref", "-d", "HEAD");
            Check(await new SourceGit.Commands.Checkout(unborn).BranchAsync("restored", unbornBase, true, false), "The lock must not block an unborn repository without submodules.");

            var module = Path.Combine(repository, modulePath);
            await Git(module, "config", "user.name", "Fixture");
            await Git(module, "config", "user.email", "fixture@example.test");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "second commit");
            await Git(module, "commit", "-am", "Second module commit");
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "target branch");
            await Git(repository, "add", ".");
            await Git(repository, "commit", "-m", "Target branch");
            await Git(repository, "branch", "target");
            await Git(repository, "checkout", "--no-recurse-submodules", "base");

            await Git(module, "checkout", "-b", "module-work");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "third commit");
            await Git(module, "commit", "-am", "Third module commit");
            var moduleHead = await Git(module, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "staged module work");
            await Git(module, "add", "tracked.txt");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "unstaged module work");
            File.WriteAllText(Path.Combine(module, "untracked.txt"), "keep module untracked");
            var moduleStatus = await Git(module, "status", "--porcelain=v1");
            await Git(repository, "config", "submodule.recurse", "true");

            async Task CheckModulePreserved()
            {
                Check(await Git(module, "rev-parse", "HEAD") == moduleHead, "The checked-out submodule commit must be preserved.");
                Check(await Git(module, "branch", "--show-current") == "module-work", "The submodule branch must remain attached.");
                Check(File.ReadAllText(Path.Combine(module, "tracked.txt")) == "unstaged module work", "Unstaged submodule work must be preserved.");
                Check(await Git(module, "show", ":tracked.txt") == "staged module work", "Staged submodule work must be preserved.");
                Check(await Git(module, "status", "--porcelain=v1") == moduleStatus, "Untracked submodule files must be preserved.");
            }

            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "discard parent work");
            Check(await new SourceGit.Commands.Checkout(repository).BranchAsync("target", true), "Locked branch checkout must succeed.");
            Check(File.ReadAllText(Path.Combine(repository, "tracked.txt")) == "target branch", "Parent changes must still be discarded.");
            await CheckModulePreserved();
            Check(await new SourceGit.Commands.Checkout(repository).BranchAsync("created", "base", true, false), "Creating a branch with discard must respect the lock.");
            await CheckModulePreserved();
            Check(await new SourceGit.Commands.Checkout(repository).CommitAsync(initialHead, true), "Detached checkout with discard must respect the lock.");
            await CheckModulePreserved();

            var changes = new List<Change>
            {
                new() { Path = "tracked.txt", WorkTree = ChangeState.Modified },
                new() { Path = modulePath, WorkTree = ChangeState.Modified },
            };
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "discard selected parent work");
            await SourceGit.Commands.Discard.ChangesAsync(repository, changes, null);
            Check(File.ReadAllText(Path.Combine(repository, "tracked.txt")) == "fixture", "Selected parent files must still be discarded.");
            await CheckModulePreserved();

            var detached = Path.Combine(repository, "Detached repository");
            await Init(detached);
            File.WriteAllText(Path.Combine(detached, "tracked.txt"), "keep detached work");
            var detachedChanges = new List<Change> { new() { Path = "Detached repository/", WorkTree = ChangeState.Untracked } };
            await SourceGit.Commands.Discard.ChangesAsync(repository, detachedChanges, null);
            Check(Directory.Exists(detached), "Selected untracked repositories must respect the lock.");

            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "discard all parent work");
            File.WriteAllText(Path.Combine(repository, "untracked.txt"), "remove parent untracked");
            File.WriteAllText(Path.Combine(repository, "file.ignored"), "remove parent ignored");
            await SourceGit.Commands.Discard.AllAsync(repository, true, true, true, null);
            Check(File.ReadAllText(Path.Combine(repository, "tracked.txt")) == "fixture", "Discard all must restore parent files.");
            Check(!File.Exists(Path.Combine(repository, "untracked.txt")) && !File.Exists(Path.Combine(repository, "file.ignored")), "Discard all must still clean parent untracked and ignored files.");
            Check(File.ReadAllText(Path.Combine(detached, "tracked.txt")) == "keep detached work", "Discard all must preserve detached repositories.");
            await CheckModulePreserved();

            var blob = await Git(repository, "hash-object", "-w", "tracked.txt");
            await Git(repository, "update-index", "--cacheinfo", $"100644,{blob},{modulePath}");
            var replacementTree = await Git(repository, "write-tree");
            var replacement = await Git(repository, "commit-tree", replacementTree, "-p", initialHead, "-m", "Replace submodule with file");
            await Git(repository, "branch", "replacement", replacement);
            await Git(repository, "reset", "--mixed", "--no-recurse-submodules", "HEAD");
            Check(!await new SourceGit.Commands.Checkout(repository).BranchAsync("replacement", true), "The lock must reject a branch that replaces a submodule with a regular file.");
            Check(!await new SourceGit.Commands.Checkout(repository).BranchAsync("blocked", "replacement", true, false), "Branch creation must also reject submodule replacement.");
            Check(!await new SourceGit.Commands.Checkout(repository).CommitAsync(replacement, true), "Detached checkout must also reject submodule replacement.");
            Check(await Git(repository, "rev-parse", "HEAD") == initialHead, "Rejected checkout must leave the parent commit unchanged.");
            await CheckModulePreserved();

            var addedParent = Path.Combine(root, "added-submodule");
            await Init(addedParent);
            await Git(addedParent, "mv", "tracked.txt", "Client");
            await Git(addedParent, "commit", "-m", "Track Client as a file");
            File.Delete(Path.Combine(addedParent, "Client"));
            await Git(addedParent, "rm", "--cached", "Client");
            await Git(addedParent, "-c", "protocol.file.allow=always", "submodule", "add", source, "Client");
            File.WriteAllText(Path.Combine(addedParent, "Client", "tracked.txt"), "keep newly added module");
            Check(!await SourceGit.Commands.Discard.AllAsync(addedParent, true, true, false, null), "Discard all must not replace a newly added submodule with a HEAD file.");
            await SourceGit.Commands.Discard.ChangesAsync(addedParent, [new() { Path = "Client", WorkTree = ChangeState.TypeChanged }], null);
            Check(File.ReadAllText(Path.Combine(addedParent, "Client", "tracked.txt")) == "keep newly added module", "Selected type changes must also respect the lock.");

            await SourceGit.Commands.Discard.ChangesAsync(repository, changes, null, false);
            Check(await Git(module, "rev-parse", "HEAD") == await Git(repository, "rev-parse", $"HEAD:{modulePath}"), "Unlocking must allow selected submodule discard.");
            Check(File.ReadAllText(Path.Combine(module, "tracked.txt")) == "fixture", "Unlocked discard must restore submodule files.");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "discard unlocked module work");
            await SourceGit.Commands.Discard.AllAsync(repository, true, true, false, null, false);
            Check(File.ReadAllText(Path.Combine(module, "tracked.txt")) == "fixture", "Unlocked discard all must restore submodule files.");
            Check(!Directory.Exists(detached), "Unlocking must allow discarding a detached repository.");
            File.WriteAllText(Path.Combine(module, "tracked.txt"), "discard during unlocked checkout");
            Check(await new SourceGit.Commands.Checkout(repository, false).BranchAsync("target", true), "Unlocked branch checkout must succeed.");
            Check(File.ReadAllText(Path.Combine(module, "tracked.txt")) == "second commit", "Unlocked branch checkout must discard submodule work.");
        }
        finally
        {
            SourceGit.Native.OS.GitExecutable = originalGit;
            var absolute = Path.GetFullPath(root);
            Check(absolute.StartsWith(Path.Combine(Path.GetTempPath(), "SourceGit.SubmoduleDiscardChecks") + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only the test fixture.");
            foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(absolute, true);
        }
        Console.WriteLine("Submodule discard checks passed: saved lock, branch/create/detached checkout, selected/all discard, staged/unstaged/untracked work and explicit unlock.");
    }

    private static async Task Init(string repository)
    {
        Directory.CreateDirectory(repository);
        await Git(repository, "init");
        await Git(repository, "config", "user.name", "Fixture");
        await Git(repository, "config", "user.email", "fixture@example.test");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "fixture");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "*.ignored\n");
        await Git(repository, "add", ".");
        await Git(repository, "commit", "-m", "Fixture");
    }

    private static async Task<string> Git(string repository, params string[] arguments)
    {
        var result = await GitQuery.ReadAsync(repository, arguments);
        Check(result.ExitCode == 0, result.Error);
        return result.Output.Trim();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
