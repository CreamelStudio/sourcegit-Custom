using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommits : Command
    {
        public string Error { get; private set; } = string.Empty;
        public QueryCommits(string repo, string limits, bool markMerged = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"log --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s {limits}";
            _markMerged = markMerged;
        }

        public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, bool onlyCurrentBranch, bool literal = false)
        {
            var builder = new StringBuilder();
            builder.Append("log -1000 --date-order --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s ");

            if (!onlyCurrentBranch)
                builder.Append(literal ? "--all " : "--branches --remotes ");

            if (literal && (method == Models.CommitSearchMethod.ByAuthor || method == Models.CommitSearchMethod.ByMessage))
                builder.Append("--fixed-strings ");

            if (method == Models.CommitSearchMethod.ByAuthor)
            {
                builder.Append("-i --author=").Append(filter.Quoted());
            }
            else if (method == Models.CommitSearchMethod.ByMessage)
            {
                var words = filter.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                    builder.Append("--grep=").Append(word.Trim().Quoted()).Append(' ');
                builder.Append("--all-match -i");
            }
            else if (method == Models.CommitSearchMethod.ByPath)
            {
                builder.Append("-- ").Append(filter.Quoted());
            }
            else
            {
                builder.Append("-G").Append(filter.Quoted());
            }

            WorkingDirectory = repo;
            Context = repo;
            Args = builder.ToString();
            _markMerged = false;
        }

        public async Task<List<Models.Commit>> GetResultAsync()
        {
            var commits = new List<Models.Commit>();
            Error = string.Empty;
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();
                using var cancellation = CancellationToken.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                var error = proc.StandardError.ReadToEndAsync();

                var findHead = false;
                // Cancellation kills Git; drain its streams before disposing the process.
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var parts = line.Split('\0');
                    if (parts.Length != 8)
                        continue;

                    var commit = new Models.Commit() { SHA = parts[0] };
                    commit.ParseParents(parts[1]);
                    commit.ParseDecorators(parts[2]);
                    commit.Author = Models.User.FindOrAdd(parts[3]);
                    commit.AuthorTime = ulong.Parse(parts[4]);
                    commit.Committer = Models.User.FindOrAdd(parts[5]);
                    commit.CommitterTime = ulong.Parse(parts[6]);
                    commit.Subject = parts[7];
                    commits.Add(commit);

                    if (!findHead && commit.IsMerged)
                        findHead = true;
                }

                await proc.WaitForExitAsync().ConfigureAwait(false);
                var stderr = await error.ConfigureAwait(false);
                CancellationToken.ThrowIfCancellationRequested();
                if (proc.ExitCode != 0)
                {
                    Error = stderr.Trim();
                    if (RaiseError)
                        RaiseException(Error);
                }

                if (_markMerged && !findHead && commits.Count > 0)
                {
                    var set = await new QueryCurrentBranchCommitHashes(WorkingDirectory, commits[^1].CommitterTime)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    foreach (var c in commits)
                    {
                        if (set.Contains(c.SHA))
                        {
                            c.IsMerged = true;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Error = $"Failed to query commits. Reason: {e.Message}";
                if (RaiseError)
                    RaiseException(Error);
            }

            return commits;
        }

        public static async Task<List<Models.Commit>> SearchAsync(string repository, string filter,
            Models.CommitSearchMethod method, bool onlyCurrentBranch, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            filter = filter?.Trim();
            if (string.IsNullOrEmpty(filter))
                return [];

            var results = new List<Models.Commit>();
            if (method is Models.CommitSearchMethod.All or Models.CommitSearchMethod.BySHA)
            {
                if (filter.Length is >= 4 and <= 64 && filter.All(Uri.IsHexDigit))
                {
                    var resolved = await Models.GitQuery.ReadAsync(repository,
                        ["rev-parse", "--verify", "--quiet", "--end-of-options", filter.ToLowerInvariant() + "^{commit}"], token).ConfigureAwait(false);
                    if (resolved.ExitCode == 0)
                    {
                        var sha = resolved.Output.Trim();
                        var belongs = !onlyCurrentBranch || (await Models.GitQuery.ReadAsync(repository,
                            ["merge-base", "--is-ancestor", sha, "HEAD"], token).ConfigureAwait(false)).ExitCode == 0;
                        if (belongs)
                            results.AddRange(await ReadSearchQueryAsync(new QueryCommits(repository, $"-1 {sha}", false) { CancellationToken = token }).ConfigureAwait(false));
                    }
                }
                if (method == Models.CommitSearchMethod.BySHA)
                    return results;
            }

            if (method == Models.CommitSearchMethod.All)
            {
                results.AddRange(await ReadSearchQueryAsync(new QueryCommits(repository, filter, Models.CommitSearchMethod.ByMessage, onlyCurrentBranch, true)
                    { CancellationToken = token }).ConfigureAwait(false));
                results.AddRange(await ReadSearchQueryAsync(new QueryCommits(repository, filter, Models.CommitSearchMethod.ByAuthor, onlyCurrentBranch, true)
                    { CancellationToken = token }).ConfigureAwait(false));
                return OrderSearchResults(results);
            }

            return await ReadSearchQueryAsync(new QueryCommits(repository, filter, method, onlyCurrentBranch, true)
                { CancellationToken = token }).ConfigureAwait(false);
        }

        private static async Task<List<Models.Commit>> ReadSearchQueryAsync(QueryCommits query)
        {
            query.RaiseError = false;
            var commits = await query.GetResultAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(query.Error))
                throw new InvalidOperationException(query.Error);
            return commits;
        }

        private static List<Models.Commit> OrderSearchResults(List<Models.Commit> commits)
        {
            var byHash = commits.DistinctBy(c => c.SHA).ToDictionary(c => c.SHA);
            var children = byHash.Keys.ToDictionary(sha => sha, _ => 0);
            foreach (var commit in byHash.Values)
                foreach (var parent in commit.Parents)
                    if (children.TryGetValue(parent, out var childCount))
                        children[parent] = childCount + 1;

            // A clock-skewed parent still belongs below its child in the filtered graph.
            var ready = new PriorityQueue<Models.Commit, (ulong Time, string SHA)>();
            foreach (var commit in byHash.Values)
                if (children[commit.SHA] == 0)
                    ready.Enqueue(commit, (ulong.MaxValue - commit.CommitterTime, commit.SHA));
            var ordered = new List<Models.Commit>();
            while (ordered.Count < 1000 && ready.TryDequeue(out var commit, out _))
            {
                ordered.Add(commit);
                foreach (var parent in commit.Parents)
                {
                    if (!children.TryGetValue(parent, out var count))
                        continue;
                    children[parent] = --count;
                    if (count == 0)
                    {
                        var next = byHash[parent];
                        ready.Enqueue(next, (ulong.MaxValue - next.CommitterTime, next.SHA));
                    }
                }
            }
            return ordered;
        }

        private bool _markMerged = false;
    }
}
