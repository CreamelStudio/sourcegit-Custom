using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SourceGit;
using SourceGit.Models;

internal static class WorkspaceChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-workspace-" + Guid.NewGuid().ToString("N"));
        var originalGit = SourceGit.Native.OS.GitExecutable;
        var originalRoot = BackgroundTasks.StorageRootOverride;
        var originalProtocol = Environment.GetEnvironmentVariable("GIT_ALLOW_PROTOCOL");
        Directory.CreateDirectory(root);
        try
        {
            SourceGit.Native.OS.GitExecutable = SourceGit.Native.OS.FindGitExecutable();
            BackgroundTasks.StorageRootOverride = Path.Combine(root, "worker");
            Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", "file");
            var child = Path.Combine(root, "child source");
            var parent = Path.Combine(root, "AU server");
            await Init(child);
            await Init(parent);
            File.WriteAllText(Path.Combine(child, "client.txt"), "needle child\n");
            await Git(child, "add", ".");
            await Git(child, "commit", "-m", "needle child commit");
            File.WriteAllText(Path.Combine(parent, "server.txt"), "first\nneedle server\n");
            await Git(parent, "add", ".");
            await Git(parent, "commit", "-m", "needle parent commit");
            await Git(parent, "-c", "protocol.file.allow=always", "submodule", "add", child, "Client folder");
            await Git(parent, "commit", "-am", "Add Client");
            File.WriteAllText(Path.Combine(parent, "untracked note.txt"), "keep this");
            await Git(parent, "config", "--file", ".gitmodules", "submodule.outside.path", "../child source");

            var repositories = await WorkspaceSearch.FindRepositoriesAsync([parent, parent, "", Path.Combine(root, "missing")], CancellationToken.None);
            Check(repositories.Count == 2 && repositories.Contains(Path.Combine(parent, "Client folder")), "Discover initialized submodules with spaces, deduplicate roots and reject escaping module paths.");
            var commits = await WorkspaceSearch.SearchAsync(parent, WorkspaceSearchKind.Commits, "needle", CancellationToken.None);
            Check(commits.Hits.Count == 1 && commits.Hits[0].SHA.Length >= 40, "Search commit messages literally.");
            var files = await WorkspaceSearch.SearchAsync(parent, WorkspaceSearchKind.Files, "note", CancellationToken.None);
            Check(files.Hits.Count == 1 && files.Hits[0].Location == "untracked note.txt", "File search includes untracked non-ignored files.");
            var text = await WorkspaceSearch.SearchAsync(parent, WorkspaceSearchKind.Text, "needle", CancellationToken.None);
            Check(text.Hits.Count == 1 && text.Hits[0].Line == 2 && text.Hits[0].Location == "server.txt", "Decode NUL-delimited grep paths and line numbers.");
            var noHit = await WorkspaceSearch.SearchAsync(parent, WorkspaceSearchKind.Text, "--not-a-git-option", CancellationToken.None);
            Check(noHit.Hits.Count == 0, "Search terms beginning with a dash must be data, not Git options.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await WorkspaceSearch.SearchAsync(parent, WorkspaceSearchKind.Text, "needle", cancelled.Token); throw new Exception("Search ignored cancellation."); }
                catch (OperationCanceledException) { }
            }
            var originalHead = (await Git(parent, "rev-parse", "HEAD")).Trim();
            await Git(parent, "branch", "other");
            Check(await new SourceGit.Commands.Checkout(parent).BranchAsync("other", false), "Checkout succeeds while preserving a recovery point.");
            var points = await RecoveryTimeline.ReadAsync(parent, CancellationToken.None);
            Check(points.Any(p => p.Source == "SourceGit" && p.SHA == originalHead && p.Description.StartsWith("checkout")), "Pin the commit before changing branches.");
            Check(points.Any(p => p.Source == "Git reflog" && p.Description.Contains("checkout")), "Include actual Git reflog history.");
            File.WriteAllText(Path.Combine(parent, "server.txt"), "uncommitted work");
            var branch = await RecoveryTimeline.CreateBranchAsync(parent, points[0], CancellationToken.None);
            Check((await Git(parent, "rev-parse", branch)).Trim() == points[0].SHA && File.ReadAllText(Path.Combine(parent, "server.txt")) == "uncommitted work" &&
                (await Git(parent, "branch", "--show-current")).Trim() == "other", "Recovery creates a branch without switching, resetting or losing uncommitted/untracked files.");

            var common = await new SourceGit.Commands.QueryGitCommonDir(parent).GetResultAsync();
            using (var lease = ProcessFileLock.TryOpen(ProcessFileLock.RepositoryPath(common)))
            using (var cancelled = new CancellationTokenSource(150))
            {
                Check(lease != null, "Obtain a cross-process repository lease.");
                try { using var blocked = await FetchScheduler.ForRepository(common).EnterRemoteOperationAsync(cancelled.Token); throw new Exception("Cross-process lock was ignored."); }
                catch (OperationCanceledException) { }
            }

            var request = new BackgroundCloneRequest
            {
                ParentFolder = root, LocalName = "background clone", Remote = parent,
                GitExecutable = SourceGit.Native.OS.GitExecutable, CredentialHelper = string.Empty, Submodules = true,
            };
            Queue(request);
            await BackgroundTasks.RunAsync(BackgroundTasks.Root);
            var state = BackgroundTasks.Read(BackgroundTasks.StatePath(request.Id), JsonCodeGen.Default.BackgroundTaskState);
            Check(state?.Status == BackgroundTaskStatus.Succeeded && File.Exists(Path.Combine(request.Target, "Client folder", "client.txt")), "Headless clone completes its recursive submodule stage.");
            await DetachedWorkerAsync(root, parent);
            var cancelledRequest = new BackgroundCloneRequest { ParentFolder = root, LocalName = "cancelled", Remote = parent, GitExecutable = request.GitExecutable };
            Queue(cancelledRequest);
            BackgroundTasks.Cancel(cancelledRequest.Id);
            await BackgroundTasks.RunAsync(BackgroundTasks.Root);
            Check(BackgroundTasks.Read(BackgroundTasks.StatePath(cancelledRequest.Id), JsonCodeGen.Default.BackgroundTaskState)?.Status == BackgroundTaskStatus.Cancelled, "Cancel a queued clone without deleting user files.");
            var interrupted = new BackgroundTaskState { Id = Guid.NewGuid().ToString("N"), Repository = parent, Status = BackgroundTaskStatus.Running };
            BackgroundTasks.Write(BackgroundTasks.StatePath(interrupted.Id), interrupted, JsonCodeGen.Default.BackgroundTaskState);
            await BackgroundTasks.RunAsync(BackgroundTasks.Root);
            Check(BackgroundTasks.Read(BackgroundTasks.StatePath(interrupted.Id), JsonCodeGen.Default.BackgroundTaskState)?.Status == BackgroundTaskStatus.Interrupted && File.Exists(Path.Combine(parent, "untracked note.txt")), "Record interrupted work without restarting into or deleting an existing folder.");
            request.LocalName = "../outside";
            try { BackgroundTasks.Validate(request); throw new Exception("Destination traversal accepted."); } catch (ArgumentException) { }
            request.LocalName = "safe";
            request.Remote = "https://token@example.com/repo.git";
            try { BackgroundTasks.Validate(request); throw new Exception("Credential URL persisted."); } catch (ArgumentException) { }

            var fetchedRepo = Path.Combine(root, "fetch clone");
            await Git(root, "clone", parent, fetchedRepo);
            await Git(fetchedRepo, "config", "remote.origin.disableautofetch", "true");
            var fetchCommon = await new SourceGit.Commands.QueryGitCommonDir(fetchedRepo).GetResultAsync();
            var fetchStatePath = BackgroundTasks.StatePath(BackgroundTasks.FetchId(fetchCommon));
            await BackgroundTasks.RunFetchAsync(fetchedRepo, 10, CancellationToken.None);
            Check(!File.Exists(fetchStatePath), "Background Fetch respects a disabled remote.");
            await Git(fetchedRepo, "config", "remote.origin.disableautofetch", "false");
            BackgroundTasks.SetRepositoryBusy(fetchCommon, true);
            await BackgroundTasks.RunFetchAsync(fetchedRepo, 10, CancellationToken.None);
            Check(!File.Exists(fetchStatePath), "Background Fetch pauses while the UI owns a busy operation.");
            BackgroundTasks.SetRepositoryBusy(fetchCommon, false);
            await BackgroundTasks.RunFetchAsync(fetchedRepo, 10, CancellationToken.None);
            var fetched = BackgroundTasks.Read(fetchStatePath, JsonCodeGen.Default.BackgroundTaskState);
            Check(fetched?.Status == BackgroundTaskStatus.Succeeded, "Background Fetch executes successfully with existing local credentials.");
            await BackgroundTasks.RunFetchAsync(fetchedRepo, 10, CancellationToken.None);
            Check(BackgroundTasks.Read(fetchStatePath, JsonCodeGen.Default.BackgroundTaskState).UpdatedAt == fetched.UpdatedAt, "A repeated scheduler tick must not repeat a recent fetch.");
            Console.WriteLine("Workspace checks passed: recovery refs/reflog and safe recovery branches; multi-repo commit/file/text search; cancellation; process locks; headless clone/submodules, cancellation and interrupted work.");
        }
        finally
        {
            SourceGit.Native.OS.GitExecutable = originalGit;
            BackgroundTasks.StorageRootOverride = originalRoot;
            Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", originalProtocol);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    public static async Task SubmitAsync(string root, string executable, string remote, string localName)
    {
        BackgroundTasks.StorageRootOverride = root;
        BackgroundTasks.ExecutableOverride = executable;
        SourceGit.Native.OS.GitExecutable = SourceGit.Native.OS.FindGitExecutable();
        await BackgroundTasks.EnqueueCloneAsync(new BackgroundCloneRequest
        {
            GitExecutable = SourceGit.Native.OS.GitExecutable, CredentialHelper = string.Empty,
            ParentFolder = Path.GetDirectoryName(root), LocalName = localName, Remote = remote,
            ExtraArgs = "--no-local", Submodules = true,
        });
    }

    private static async Task DetachedWorkerAsync(string root, string remote)
    {
        var workerRoot = Path.Combine(root, "detached-worker");
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SourceGit.exe" : "SourceGit");
        Check(File.Exists(executable), "The product apphost is available for a real detached worker test.");
        BackgroundTasks.Write(Path.Combine(workerRoot, "fetch.json"), new BackgroundFetchSettings
        {
            Enabled = true, GitExecutable = SourceGit.Native.OS.GitExecutable, Repositories = [remote],
        }, JsonCodeGen.Default.BackgroundFetchSettings);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WorkspaceChecks).Assembly.Location);
        foreach (var arg in new[] { "--background-submit", workerRoot, executable, remote, "detached clone" }) start.ArgumentList.Add(arg);
        using var submitter = Process.Start(start)!;
        await submitter.WaitForExitAsync();
        Check(submitter.ExitCode == 0, "The UI-side submitter can exit after queueing the job.");
        var savedRoot = BackgroundTasks.StorageRootOverride;
        BackgroundTasks.StorageRootOverride = workerRoot;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(40);
            BackgroundTaskState state = null;
            while (DateTime.UtcNow < deadline)
            {
                state = BackgroundTasks.GetStates().FirstOrDefault();
                if (state != null && !state.IsActive) break;
                await Task.Delay(100);
            }
            Check(state?.Status == BackgroundTaskStatus.Succeeded && File.Exists(Path.Combine(root, "detached clone", "Client folder", "client.txt")), "Clone and submodules finish in the shadow worker after the submitting process has exited.");
            var pid = int.Parse(File.ReadAllText(Path.Combine(workerRoot, "worker-pid")));
            Check(pid != Environment.ProcessId && pid != submitter.Id, "The worker is a separate process.");
            var obsolete = Path.Combine(workerRoot, "runtime", "obsolete");
            var unknown = Path.Combine(workerRoot, "runtime", "unknown");
            Directory.CreateDirectory(obsolete);
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(obsolete, "SourceGit.exe"), "old runtime");
            File.WriteAllText(Path.Combine(unknown, "user-note.txt"), "preserve");
            File.WriteAllText(Path.Combine(workerRoot, "worker-version"), "0.0.0.0");
            start.ArgumentList[^1] = "after handover";
            using (var nextSubmitter = Process.Start(start)!)
            {
                await nextSubmitter.WaitForExitAsync();
                Check(nextSubmitter.ExitCode == 0, "A new UI can request a worker handover.");
            }
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !BackgroundTasks.GetStates().Any(s => s.Repository.EndsWith("after handover", StringComparison.Ordinal) && !s.IsActive)) await Task.Delay(100);
            Check(BackgroundTasks.GetStates().Any(s => s.Repository.EndsWith("after handover", StringComparison.Ordinal) && s.Status == BackgroundTaskStatus.Succeeded), "The replacement worker handles pending jobs after the old worker drains.");
            var replacementPid = int.Parse(File.ReadAllText(Path.Combine(workerRoot, "worker-pid")));
            Check(replacementPid != pid, "Worker version handover starts a fresh process.");
            Check(!Directory.Exists(obsolete) && File.Exists(Path.Combine(unknown, "user-note.txt")), "Clean obsolete flat runtimes but preserve unknown files.");
            BackgroundTasks.Write(Path.Combine(workerRoot, "fetch.json"), new BackgroundFetchSettings { Enabled = false }, JsonCodeGen.Default.BackgroundFetchSettings);
            try
            {
                using var worker = Process.GetProcessById(replacementPid);
                Check(worker.MainModule!.FileName.StartsWith(Path.Combine(workerRoot, "runtime"), StringComparison.OrdinalIgnoreCase), "The worker runs from an isolated runtime, not the UI install directory.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await worker.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { } // The worker may already have exited after draining the queue.
        }
        finally { BackgroundTasks.StorageRootOverride = savedRoot; }
        Console.WriteLine("Detached worker check passed: submitter exited; independent shadow process completed clone/submodules and exited when idle.");
    }

    private static async Task Init(string path)
    {
        Directory.CreateDirectory(path);
        await Git(path, "init");
        await Git(path, "config", "user.name", "Fixture");
        await Git(path, "config", "user.email", "fixture@example.test");
    }

    private static async Task<string> Git(string repository, params string[] args)
    {
        var result = await GitQuery.ReadAsync(repository, args);
        if (result.ExitCode != 0) throw new Exception(result.Error);
        return result.Output;
    }

    private static void Queue(BackgroundCloneRequest request)
    {
        BackgroundTasks.Write(BackgroundTasks.StatePath(request.Id), new BackgroundTaskState { Id = request.Id, Repository = request.Target, Status = BackgroundTaskStatus.Queued }, JsonCodeGen.Default.BackgroundTaskState);
        BackgroundTasks.Write(Path.Combine(BackgroundTasks.Root, "queue", request.Id + ".json"), request, JsonCodeGen.Default.BackgroundCloneRequest);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
