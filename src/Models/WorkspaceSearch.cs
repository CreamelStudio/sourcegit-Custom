using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum WorkspaceSearchKind { Commits, Files, Text }

    public sealed class WorkspaceSearchHit
    {
        public string Repository { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string SHA { get; set; } = string.Empty;
        public int Line { get; set; }
        public string RepositoryName => Path.GetFileName(Repository);
        public string LineText => Line > 0 ? Line.ToString() : string.Empty;
    }

    internal static class WorkspaceSearch
    {
        public static async Task<List<string>> FindRepositoriesAsync(IEnumerable<string> roots, CancellationToken token)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var found = new HashSet<string>(comparer);
            var pending = new Queue<string>(roots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath));
            // shortcut: Limit a workspace to 64 initialized repositories; larger workspaces need paging.
            while (pending.Count > 0 && found.Count < 64)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.TrimEndingDirectorySeparator(pending.Dequeue());
                if (found.Contains(path) || !Directory.Exists(path) ||
                    !(Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))))
                    continue;
                var top = await GitQuery.ReadAsync(path, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
                if (top.ExitCode != 0 || !comparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top.Output.Trim())), path))
                    continue;
                found.Add(path);
                if (!File.Exists(Path.Combine(path, ".gitmodules")))
                    continue;
                var modules = await GitQuery.ReadAsync(path, ["config", "-z", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$"], token).ConfigureAwait(false);
                foreach (var entry in modules.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = entry.IndexOf('\n');
                    if (separator < 0)
                        continue;
                    var relative = entry[(separator + 1)..];
                    if (Path.IsPathRooted(relative))
                        continue;
                    var child = Path.GetFullPath(Path.Combine(path, relative));
                    if (child.StartsWith(path + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        pending.Enqueue(child);
                }
            }
            return found.ToList();
        }

        public static async Task<(List<WorkspaceSearchHit> Hits, bool Limited)> SearchAsync(string repository,
            WorkspaceSearchKind kind, string query, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(query) || query.IndexOfAny(['\r', '\n', '\0']) >= 0 || query.Length > 512)
                throw new ArgumentException("Enter a single search term up to 512 characters.");
            var args = kind switch
            {
                WorkspaceSearchKind.Commits => new[] { "log", "--all", "-n", "200", "--format=%H%x00%s", "-i", "--fixed-strings", "--grep=" + query },
                WorkspaceSearchKind.Files => new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" },
                _ => new[] { "grep", "-n", "-z", "-I", "-i", "-F", "-e", query, "--" },
            };
            var result = await GitQuery.ReadAsync(repository, args, token).ConfigureAwait(false);
            if (result.ExitCode != 0 && !(kind == WorkspaceSearchKind.Text && result.ExitCode == 1) && !result.Truncated)
                throw new InvalidOperationException(result.Error.Trim());
            var hits = new List<WorkspaceSearchHit>();
            if (kind == WorkspaceSearchKind.Commits)
            {
                foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.TrimEnd('\r').Split('\0', 2);
                    if (parts.Length == 2)
                        hits.Add(new WorkspaceSearchHit { Repository = repository, SHA = parts[0], Location = parts[0][..Math.Min(8, parts[0].Length)], Text = parts[1] });
                }
            }
            else if (kind == WorkspaceSearchKind.Files)
            {
                foreach (var file in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct())
                    if (file.Contains(query, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new WorkspaceSearchHit { Repository = repository, Location = file, Text = file });
            }
            else
            {
                var offset = 0;
                while (offset < result.Output.Length && hits.Count <= 200)
                {
                    var pathEnd = result.Output.IndexOf('\0', offset);
                    var numberEnd = pathEnd < 0 ? -1 : result.Output.IndexOf('\0', pathEnd + 1);
                    var lineEnd = numberEnd < 0 ? -1 : result.Output.IndexOf('\n', numberEnd + 1);
                    if (lineEnd < 0)
                        break;
                    if (int.TryParse(result.Output[(pathEnd + 1)..numberEnd], out var number))
                        hits.Add(new WorkspaceSearchHit { Repository = repository, Location = result.Output[offset..pathEnd], Line = number, Text = result.Output[(numberEnd + 1)..lineEnd].TrimEnd('\r') });
                    offset = lineEnd + 1;
                }
            }
            return (hits.Take(200).ToList(), result.Truncated || hits.Count >= 200);
        }
    }
}
