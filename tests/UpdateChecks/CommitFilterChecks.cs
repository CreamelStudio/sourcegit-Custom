using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Commands;
using SourceGit.Models;

internal static class CommitFilterChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.CommitFilterChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalGit = SourceGit.Native.OS.GitExecutable;
        try
        {
            SourceGit.Native.OS.GitExecutable = "git";
            await Git(root, "init", "-b", "main");
            var first = await Commit(root, "Needle [Dev]", "alice@example.test", "Needle literal [fix]");
            var author = await Commit(root, "Other Person", "needle@example.test", "Unrelated title");
            var message = await Commit(root, "Bob", "bob@example.test", "Another NEEDLE change");
            var body = await Git(root, "-c", "user.name=Bob", "-c", "user.email=bob@example.test", "commit", "--allow-empty", "-m", "Header", "-m", "needle in body");
            var combined = await QueryCommits.SearchAsync(root, "needle", CommitSearchMethod.All, false);
            Check(combined.Count == 4 && combined.Select(c => c.SHA).Distinct().Count() == 4 && combined.Any(c => c.SHA == first) &&
                combined.Any(c => c.SHA == author) && combined.Any(c => c.SHA == message), "Find message/body and author/email matches, case-insensitively and without duplicate rows.");
            var literal = await QueryCommits.SearchAsync(root, "[fix]", CommitSearchMethod.ByMessage, false);
            Check(literal.Count == 1 && literal[0].SHA == first, "Treat punctuation as literal text rather than a regex.");
            Check((await QueryCommits.SearchAsync(root, "needle@example", CommitSearchMethod.ByAuthor, false)).Single().SHA == author, "Filter specifically by author email.");
            Check((await QueryCommits.SearchAsync(root, message[..10].ToUpperInvariant(), CommitSearchMethod.All, false)).Single().SHA == message, "Recognize an abbreviated hash alongside text searches.");
            Check((await QueryCommits.SearchAsync(root, first, CommitSearchMethod.BySHA, false)).Single().SHA == first, "Find a full commit hash.");
            Check((await QueryCommits.SearchAsync(root, "--all", CommitSearchMethod.All, false)).Count == 0, "A leading dash remains search data, never a Git option.");
            Check((await QueryCommits.SearchAsync(root, "not present", CommitSearchMethod.All, false)).Count == 0, "Return an empty filtered list for no matches.");
            Check((await QueryCommits.SearchAsync(root, " ", CommitSearchMethod.All, false)).Count == 0, "Empty input does not start a Git search.");

            var folder = Path.Combine(root, "folder path");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "data.txt"), "NEEDLE_CODE42");
            await Git(root, "add", ".");
            var fileCommit = await Commit(root, "Reader", "reader@example.test", "Path sample");
            Check((await QueryCommits.SearchAsync(root, "folder path/*.txt", CommitSearchMethod.ByPath, false)).Single().SHA == fileCommit,
                "Keep file-path and glob filtering in the compact search.");
            Check((await QueryCommits.SearchAsync(root, "NEEDLE_CODE[0-9]+", CommitSearchMethod.ByContent, false)).Single().SHA == fileCommit,
                "Keep changed-content regex searching in the same compact search.");
            try { await QueryCommits.SearchAsync(root, "[", CommitSearchMethod.ByContent, false); throw new Exception("Invalid content regex was silently accepted."); }
            catch (InvalidOperationException) { }

            await Git(root, "checkout", "-b", "other");
            var other = await Commit(root, "Bob", "bob@example.test", "exclusive-other");
            await Git(root, "checkout", "main");
            Check((await QueryCommits.SearchAsync(root, "exclusive-other", CommitSearchMethod.All, true)).Count == 0 &&
                (await QueryCommits.SearchAsync(root, "exclusive-other", CommitSearchMethod.All, false)).Single().SHA == other,
                "Search all history while keeping current-branch scoping correct for existing callers.");
            Check((await QueryCommits.SearchAsync(root, other[..10], CommitSearchMethod.BySHA, true)).Count == 0, "A hash outside HEAD's history must not bypass the current-branch restriction.");

            await Git(root, "checkout", "--detach", other);
            var detached = await Commit(root, "Bob", "bob@example.test", "detached-only");
            Check((await QueryCommits.SearchAsync(root, "detached-only", CommitSearchMethod.All, false)).Single().SHA == detached, "Include detached HEAD history as well as branch refs.");
            await Git(root, "checkout", "main");
            var futureParent = await Commit(root, "Clock", "clock@example.test", "clock-skew parent", "2030-01-01T12:00:00Z");
            var olderChild = await Commit(root, "Clock", "clock@example.test", "clock-skew child", "2020-01-01T12:00:00Z");
            var ordered = await QueryCommits.SearchAsync(root, "clock-skew", CommitSearchMethod.All, false);
            Check(ordered.Count == 2 && ordered[0].SHA == olderChild && ordered[1].SHA == futureParent && ordered[0].CommitterTime < ordered[1].CommitterTime,
                "Keep a child above its parent in the filtered graph even when commit clocks are reversed.");
            CommitGraph.SetDefaultPens();
            Check(CommitGraph.Generate(ordered, false, CommitGraphHighlighting.All, new HashSet<string>()).Dots.Count == 2, "Generate the graph for the actual filtered rows.");

            var repository = new SourceGit.ViewModels.Repository(false, root, Path.Combine(root, ".git"));
            var histories = new SourceGit.ViewModels.Histories(repository);
            var original = await new QueryCommits(root, "--all", false).GetResultAsync();
            histories.Commits = original;
            histories.IsCommitFilterVisible = true;
            histories.CommitFilterText = "needle";
            histories.IsCommitFilterVisible = false;
            Check(ReferenceEquals(histories.Commits, original) && !histories.HasCommitFilter && histories.Graph.Dots.Count == original.Count,
                "Closing the compact filter cancels the pending query and restores the original list and graph.");
            histories.IsCommitFilterVisible = true;
            histories.CommitFilterText = "clock";
            var refreshed = original.Take(2).ToList();
            histories.Commits = refreshed;
            histories.IsCommitFilterVisible = false;
            Check(ReferenceEquals(histories.Commits, refreshed), "A repository refresh during filtering becomes the restored source, without stale search results replacing it.");
            histories.EndCommitFilter();

            using (var inFlightCancellation = new CancellationTokenSource())
            {
                var inFlight = new QueryCommits(root, "--all", false) { CancellationToken = inFlightCancellation.Token }.GetResultAsync();
                var pending = !inFlight.IsCompleted;
                inFlightCancellation.Cancel();
                try
                {
                    await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!pending, "An active commit query must observe cancellation, not finish with stale results.");
                }
                catch (OperationCanceledException) { }
            }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await QueryCommits.SearchAsync(root, "needle", CommitSearchMethod.All, false, cancelled.Token); throw new Exception("Search ignored cancellation."); }
            catch (OperationCanceledException) { }
            try { await new QueryCommits(root, "--all") { CancellationToken = cancelled.Token }.GetResultAsync(); throw new Exception("Commit query ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            SourceGit.Native.OS.GitExecutable = originalGit;
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "SourceGit.CommitFilterChecks") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new Exception("Unexpected test cleanup path.");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        Console.WriteLine("Compact commit filter checks passed: message/body, author/email, hashes, literal input, branch/detached history, graph ordering, list restoration and cancellation. No GUI or user repository changed.");
    }

    private static async Task<string> Commit(string repository, string author, string email, string message, string date = null)
    {
        var originalAuthorDate = Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE");
        var originalCommitDate = Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE");
        try
        {
            if (date != null)
            {
                Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", date);
                Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", date);
            }
            await Git(repository, "-c", "user.name=" + author, "-c", "user.email=" + email, "commit", "--allow-empty", "-m", message);
            return (await Git(repository, "rev-parse", "HEAD")).Trim();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", originalAuthorDate);
            Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", originalCommitDate);
        }
    }

    private static async Task<string> Git(string repository, params string[] arguments)
    {
        var result = await GitQuery.ReadAsync(repository, arguments);
        Check(result.ExitCode == 0, result.Error);
        return result.Output.Trim();
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
