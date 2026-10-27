using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SourceGit.Models;

internal static class WorktreeChecks
{
    public static async Task RunAsync()
    {
        var originalGit = SourceGit.Native.OS.GitExecutable;
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.WorktreeChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SourceGit.Native.OS.GitExecutable = "git";
            var repository = Path.Combine(root, "server");
            var client = Path.Combine(root, "client");
            await Init(repository);
            await Init(client);
            await Git(repository, "-c", "protocol.file.allow=always", "submodule", "add", client, "Client");
            await Git(repository, "commit", "-am", "Add client");
            client = Path.Combine(repository, "Client");
            var head = await Git(repository, "rev-parse", "HEAD");
            var clientHead = await Git(client, "rev-parse", "HEAD");
            var currentBranch = await Git(repository, "branch", "--show-current");

            var magic = Path.Combine(root, "Magic folder 마법고");
            await Git(repository, "worktree", "add", "-b", "magic-2026", magic);
            await Git(client, "worktree", "add", "-b", "magic-2026", Path.Combine(magic, "Client"));
            var live = Path.Combine(root, "live folder");
            await Git(repository, "worktree", "add", "-b", "live", live);
            File.WriteAllText(Path.Combine(live, "tracked.txt"), "modified live worktree");
            File.WriteAllText(Path.Combine(live, "untracked.txt"), "keep live file");
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "modified main worktree");
            File.WriteAllText(Path.Combine(repository, "untracked.txt"), "keep main file");
            var locked = Path.Combine(root, "locked missing");
            await Git(repository, "worktree", "add", "--lock", "-b", "locked", locked);
            DeleteFixture(root, magic);
            DeleteFixture(root, locked);

            var branches = await new SourceGit.Commands.QueryBranches(repository).GetResultAsync();
            Check(string.IsNullOrEmpty(branches.Single(b => b.Name == "magic-2026").WorktreePath), "Refreshing branches releases a deleted worktree immediately, without waiting for Git GC.");
            var clientBranches = await new SourceGit.Commands.QueryBranches(client).GetResultAsync();
            Check(string.IsNullOrEmpty(clientBranches.Single(b => b.Name == "magic-2026").WorktreePath), "The independently registered Client worktree is also cleaned when its repository is refreshed.");
            Check(await Git(repository, "rev-parse", "magic-2026") == head && await Git(client, "rev-parse", "magic-2026") == clientHead, "Pruning retains both branch commits.");
            var worktrees = await new SourceGit.Commands.Worktree(repository).ReadAllAsync();
            Check(worktrees.Count == 3 && worktrees.Any(w => w.IsLocked && w.Branch == "refs/heads/locked") && worktrees.Any(w => w.FullPath == live.Replace('\\', '/')), "Live and locked missing worktrees remain registered; spaces and Unicode paths are decoded.");
            Check(File.ReadAllText(Path.Combine(live, "tracked.txt")) == "modified live worktree" && File.Exists(Path.Combine(live, "untracked.txt")) &&
                File.ReadAllText(Path.Combine(repository, "tracked.txt")) == "modified main worktree" && File.Exists(Path.Combine(repository, "untracked.txt")) &&
                await Git(repository, "branch", "--show-current") == currentBranch, "Automatic cleanup preserves dirty files, untracked files and the selected branch.");
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "fixture");

            var checkout = Path.Combine(root, "checkout missing");
            await Git(repository, "worktree", "add", "-b", "checkout-missing", checkout);
            DeleteFixture(root, checkout);
            Check(await new SourceGit.Commands.Checkout(repository) { RaiseError = false }.BranchAsync("checkout-missing", false), "Checkout repairs a newly deleted registration without requiring a preceding refresh.");
            Check(await new SourceGit.Commands.Checkout(repository) { RaiseError = false }.BranchAsync(currentBranch, false), "Return to the fixture branch.");

            var deletion = Path.Combine(root, "delete missing");
            await Git(repository, "worktree", "add", "-b", "delete-missing", deletion);
            DeleteFixture(root, deletion);
            Check(await new SourceGit.Commands.Branch(repository, "delete-missing") { RaiseError = false }.DeleteLocalAsync(false), "Deleting a merged branch is no longer blocked by a missing worktree.");

            var oldPath = Path.Combine(root, "reuse old");
            var newPath = Path.Combine(root, "reuse new");
            await Git(repository, "worktree", "add", "-b", "reuse", oldPath);
            DeleteFixture(root, oldPath);
            Check(await new SourceGit.Commands.Worktree(repository) { RaiseError = false }.AddWithLocalBranchAsync(newPath, "reuse"), "The same branch can be opened as a worktree at a new location.");

            var detached = Path.Combine(root, "detached missing");
            var deferred = Path.Combine(root, "deferred missing");
            await Git(repository, "worktree", "add", "--detach", detached);
            await Git(repository, "worktree", "add", "-b", "deferred", deferred);
            DeleteFixture(root, detached);
            DeleteFixture(root, deferred);
            worktrees = await new SourceGit.Commands.Worktree(repository).ReadAllAsync();
            Check(worktrees.Any(w => w.IsDetached && w.IsPrunable) && worktrees.Any(w => w.Branch == "refs/heads/deferred"), "If any candidate is unsafe, defer the batch rather than pruning detached commits.");
            await Git(repository, "worktree", "lock", detached);
            worktrees = await new SourceGit.Commands.Worktree(repository).ReadAllAsync();
            Check(!worktrees.Any(w => w.Branch == "refs/heads/deferred") && worktrees.Any(w => w.IsLocked && w.IsDetached), "A locked detached worktree is preserved while other safe stale registrations are pruned.");

            var incomplete = Path.Combine(root, "existing without git file");
            await Git(repository, "worktree", "add", "-b", "incomplete", incomplete);
            File.Delete(Path.Combine(incomplete, ".git"));
            worktrees = await new SourceGit.Commands.Worktree(repository).ReadAllAsync();
            Check(worktrees.Any(w => w.Branch == "refs/heads/incomplete") && File.ReadAllText(Path.Combine(incomplete, "tracked.txt")) == "fixture", "An existing folder with missing Git linkage must remain untouched.");
        }
        finally
        {
            SourceGit.Native.OS.GitExecutable = originalGit;
            DeleteFixture(root, root);
        }
        Console.WriteLine("Worktree checks passed: stale server/Client registrations, direct checkout/delete/add, branch/file preservation, locked and detached worktrees, spaces/Unicode and incomplete folders.");
    }

    private static async Task Init(string repository)
    {
        Directory.CreateDirectory(repository);
        await Git(repository, "init");
        await Git(repository, "config", "user.name", "Fixture");
        await Git(repository, "config", "user.email", "fixture@example.test");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "fixture");
        await Git(repository, "add", ".");
        await Git(repository, "commit", "-m", "Fixture");
    }

    private static async Task<string> Git(string repository, params string[] arguments)
    {
        var result = await GitQuery.ReadAsync(repository, arguments);
        Check(result.ExitCode == 0, result.Error);
        return result.Output.Trim();
    }

    private static void DeleteFixture(string root, string path)
    {
        var absolute = Path.GetFullPath(path);
        var absoluteRoot = Path.GetFullPath(root);
        Check(absolute == absoluteRoot || absolute.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only this test's temporary fixture.");
        if (!Directory.Exists(absolute))
            return;
        foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(absolute, true);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
