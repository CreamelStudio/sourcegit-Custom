using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SourceGit;
using SourceGit.Models;

internal static class FetchChecks
{
    public static async Task RunAsync()
    {
        var opened = DateTimeOffset.UtcNow;
        var schedule = new FetchScheduler(opened);
        Check(schedule.NextFetchAt(10) == opened.AddMinutes(1), "The first automatic fetch is due after one minute.");
        schedule.MarkFetched(opened.AddMinutes(1));
        Check(schedule.NextFetchAt(10) == opened.AddMinutes(11), "Successful fetches use the configured interval.");
        schedule.MarkFailed(opened.AddMinutes(11));
        Check(schedule.LastSuccess == opened.AddMinutes(1), "Failure must not overwrite the last successful fetch.");
        Check(schedule.NextFetchAt(10) == opened.AddMinutes(12), "Failures retry after one minute, not every timer tick.");
        schedule.MarkFetched(opened.AddMinutes(12));
        Check(schedule.LastFailure == null && schedule.NextFetchAt(10) == opened.AddMinutes(22), "Success clears the retry schedule.");
        Check(schedule.NextFetchAt(0) == opened.AddMinutes(13) && schedule.NextFetchAt(100) == opened.AddMinutes(72), "Clamp persisted intervals to the supported range.");

        var defaults = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.Preferences)!;
        var existing = JsonSerializer.Deserialize("""{"EnableAutoFetch":false,"AutoFetchInterval":10}""", JsonCodeGen.Default.Preferences)!;
        Check(defaults.EnableAutoFetch && !existing.EnableAutoFetch && existing.AutoFetchInterval == 10, "Enable new defaults while preserving existing opt-outs.");
        var notified = false;
        existing.PropertyChanged += (_, e) => notified |= e.PropertyName == "EnableAutoFetch";
        existing.EnableAutoFetch = true;
        Check(notified, "Changing the setting must update open repository status bars.");

        var schedulers = Enumerable.Range(0, 5).Select(_ => new FetchScheduler(opened)).ToArray();
        var slots = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 4; i++)
                slots.Add(await schedulers[i].TryEnterAutomaticAsync(CancellationToken.None));
            Check(slots.All(s => s != null), "Independent repositories may fetch concurrently.");
            Check(await schedulers[0].TryEnterAutomaticAsync(CancellationToken.None) == null, "A repository cannot queue duplicate automatic fetches.");
            using var cancellation = new CancellationTokenSource();
            var fifth = schedulers[4].TryEnterAutomaticAsync(cancellation.Token);
            Check(!fifth.IsCompleted, "A fifth automatic fetch must wait for one of the four slots.");
            cancellation.Cancel();
            try { await fifth; throw new Exception("Queued fetch ignored cancellation."); }
            catch (OperationCanceledException) { }
            var retry = schedulers[4].TryEnterAutomaticAsync(CancellationToken.None);
            Check(!retry.IsCompleted, "A canceled job must release its pending marker without inventing a slot.");
            slots[0].Dispose();
            using var admitted = await retry.WaitAsync(TimeSpan.FromSeconds(5));
            Check(admitted != null, "A canceled repository can be scheduled again.");
            var busy = new FetchScheduler(opened);
            var pending = busy.TryEnterAutomaticAsync(CancellationToken.None);
            using (var manual = await busy.EnterRemoteOperationAsync(CancellationToken.None))
            {
                admitted.Dispose();
                Check(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == null, "Recheck remote activity after waiting for a global slot.");
            }
            using var rescheduled = await busy.TryEnterAutomaticAsync(CancellationToken.None);
            Check(rescheduled != null, "Skipping a busy repository must release its slot and pending marker.");
        }
        finally
        {
            foreach (var slot in slots)
                slot?.Dispose();
        }

        using (var manual = await schedule.EnterRemoteOperationAsync(CancellationToken.None))
        {
            Check(await schedule.TryEnterAutomaticAsync(CancellationToken.None) == null, "Skip automatic fetch while a remote operation is running.");
            using var cancellation = new CancellationTokenSource();
            var queued = schedule.EnterRemoteOperationAsync(cancellation.Token);
            Check(!queued.IsCompleted, "Operations on the same Git directory must be serialized.");
            cancellation.Cancel();
            try { await queued; throw new Exception("Queued remote operation ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        using (var available = await schedule.EnterRemoteOperationAsync(CancellationToken.None)) { }

        await CheckLocalGitAsync();
        Console.WriteLine("Fetch checks passed: first-run timing, intervals, retry, preferences, four parallel slots, duplicate prevention, cancellation, worktree serialization and real Git fetch/prune.");
    }

    private static async Task CheckLocalGitAsync()
    {
        SourceGit.Native.OS.GitExecutable = "git";
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.FetchChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var origin = Path.Combine(root, "origin.git");
            var writer = Path.Combine(root, "writer");
            var reader = Path.Combine(root, "reader");
            var worktree = Path.Combine(root, "worktree");
            await Git(root, "init", "--bare", origin);
            await Git(root, "clone", origin, writer);
            await Git(writer, "config", "user.name", "Fetch fixture");
            await Git(writer, "config", "user.email", "fetch-fixture@example.invalid");
            await Git(writer, "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Initial");
            await Git(writer, "branch", "-M", "main");
            await Git(writer, "push", "origin", "main");
            await Git(origin, "symbolic-ref", "HEAD", "refs/heads/main");
            await Git(writer, "push", "origin", "main:obsolete");
            await Git(root, "clone", origin, reader);
            await Git(reader, "worktree", "add", "-b", "fixture-worktree", worktree);
            await Git(writer, "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "New remote commit");
            await Git(writer, "push", "origin", "main", ":obsolete");

            var remote = new Remote { Name = "origin", PrivateSSHKey = "fixture-key" };
            var auto = new SourceGit.Commands.Fetch(reader, remote);
            Check(auto.Args.Contains("--prune", StringComparison.Ordinal) && auto.SSHKey == remote.PrivateSSHKey && !auto.AllowInteractiveAuthentication, "Auto fetch prunes, preserves the remote SSH key and suppresses prompts.");
            var backgroundInfo = new InspectableFetch(reader, remote).StartInfo();
            Check(backgroundInfo.Environment.ContainsKey("GIT_ASKPASS") && string.IsNullOrEmpty(backgroundInfo.Environment["GIT_ASKPASS"]) && backgroundInfo.Environment["GIT_TERMINAL_PROMPT"] == "0" &&
                backgroundInfo.Environment["GCM_INTERACTIVE"] == "0" && backgroundInfo.Environment["SSH_ASKPASS_REQUIRE"] == "never",
                "Background authentication must not open Git, SSH or credential manager prompts.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Check(await auto.WithCancellation(timeout.Token).ExecAsync(), "Fetch the real local remote.");
            Check(await Git(reader, "rev-parse", "refs/remotes/origin/main") == await Git(writer, "rev-parse", "HEAD"), "Fetch must receive new remote commits.");
            Check(string.IsNullOrWhiteSpace(await Git(reader, "for-each-ref", "refs/remotes/origin/obsolete")), "Auto prune removes a deleted remote tracking branch.");
            Check(await Git(reader, "rev-parse", "HEAD") != await Git(writer, "rev-parse", "HEAD"), "Automatic fetch must not change the checked-out commit.");

            var common = await new SourceGit.Commands.QueryGitCommonDir(reader).GetResultAsync();
            var otherCommon = await new SourceGit.Commands.QueryGitCommonDir(worktree).GetResultAsync();
            var scheduler = FetchScheduler.ForRepository(common);
            Check(ReferenceEquals(scheduler, FetchScheduler.ForRepository(otherCommon)), "Linked worktrees share one schedule and remote operation lock.");
            Task<bool> queued;
            using (var held = await scheduler.EnterRemoteOperationAsync(timeout.Token))
            {
                queued = new SourceGit.Commands.Fetch(worktree, new Remote { Name = "origin" }).WithCancellation(timeout.Token).ExecAsync();
                Check(!queued.IsCompleted, "A fetch in a linked worktree must wait for the existing remote operation.");
                using var canceled = new CancellationTokenSource();
                var waiting = new SourceGit.Commands.Fetch(reader, remote).WithCancellation(canceled.Token).ExecAsync();
                canceled.Cancel();
                Check(!await waiting, "A canceled command waiting for the repository lock must return false.");
            }
            Check(await queued.WaitAsync(TimeSpan.FromSeconds(20)), "A queued worktree fetch must finish once the lock is released.");
            Check(!await new SourceGit.Commands.Fetch(reader, new Remote { Name = "missing-remote" }).WithCancellation(timeout.Token).ExecAsync(), "A failed fetch returns false.");

            var started = Path.Combine(root, "upload-pack-started");
            var script = Path.Combine(root, "slow-upload-pack.sh");
            await File.WriteAllTextAsync(script, $"#!/bin/sh\nprintf ready > '{started.Replace('\\', '/')}'\nwhile :; do sleep 1; done\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var runningCancellation = new CancellationTokenSource();
            var runningLog = new RecordingLog();
            var slow = new SourceGit.Commands.Fetch(reader, new Remote { Name = "origin" })
            {
                Args = $"fetch --upload-pack={script.Replace('\\', '/').Quoted()} origin",
            }.WithCancellation(runningCancellation.Token).Use(runningLog).ExecAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(started) && !slow.IsCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                Check(File.Exists(started), "The slow fetch fixture must actually start its remote process: " + string.Join('\n', runningLog.Lines));
            }
            finally
            {
                await runningCancellation.CancelAsync();
            }
            Check(!await slow.WaitAsync(TimeSpan.FromSeconds(10)), "Canceling a running fetch must terminate the process and report failure.");
            using var afterCancellation = await scheduler.EnterRemoteOperationAsync(timeout.Token);
        }
        finally
        {
            // Git object files can be read-only on Windows.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private sealed class RecordingLog : ICommandLog
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public void AppendLine(string line) => Lines.Enqueue(line);
    }

    private sealed class InspectableFetch(string repository, Remote remote) : SourceGit.Commands.Fetch(repository, remote)
    {
        public ProcessStartInfo StartInfo() => CreateGitStartInfo(true);
    }

    private static async Task<string> Git(string directory, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }};
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, $"Git fixture failed: {string.Join(' ', args)}: {await error}");
        return (await output).Trim();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
