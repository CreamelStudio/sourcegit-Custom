using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Worktree : Command
    {
        public Worktree(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<List<Models.Worktree>> ReadAllAsync()
        {
            var worktrees = await ReadRegisteredAsync().ConfigureAwait(false);
            var hasMissing = false;
            for (var i = 1; i < worktrees.Count; i++)
            {
                var worktree = worktrees[i];
                if (!worktree.IsPrunable || worktree.IsLocked)
                    continue;

                // Git prunes the entire set, so defer if any candidate cannot safely be unregistered.
                if (!CanRemoveMissing(worktree))
                    return worktrees;
                hasMissing = true;
            }

            if (!hasMissing)
                return worktrees;

            try
            {
                // Prune only removes administrative records, even if a folder reappears during the query.
                var result = await Models.GitQuery.ReadAsync(WorkingDirectory,
                    ["worktree", "prune", "--verbose", "--expire", "now"], CancellationToken).ConfigureAwait(false);
                Log?.AppendLine(result.Output + result.Error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Log?.AppendLine($"Could not prune stale worktree registrations: {ex.Message}");
            }

            return await ReadRegisteredAsync().ConfigureAwait(false);
        }

        private static bool CanRemoveMissing(Models.Worktree worktree)
        {
            if (!worktree.IsPrunable || worktree.IsLocked || worktree.IsBare || worktree.IsDetached ||
                !worktree.Branch.StartsWith("refs/heads/", StringComparison.Ordinal) || !Path.IsPathFullyQualified(worktree.FullPath))
                return false;

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(worktree.FullPath));
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    return false;

                try { File.GetAttributes(worktree.FullPath); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }

            return false;
        }

        private async Task<List<Models.Worktree>> ReadRegisteredAsync()
        {
            Args = "worktree list --porcelain -z --expire now";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            var worktrees = new List<Models.Worktree>();
            Models.Worktree last = null;
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    {
                        last = new Models.Worktree() { FullPath = line.Substring(9) };
                        worktrees.Add(last);
                        continue;
                    }

                    if (last == null)
                        continue;

                    if (line.StartsWith("bare", StringComparison.Ordinal))
                    {
                        last.IsBare = true;
                    }
                    else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                    {
                        last.Head = line.Substring(5).Trim();
                    }
                    else if (line.StartsWith("branch ", StringComparison.Ordinal))
                    {
                        last.Branch = line.Substring(7).Trim();
                    }
                    else if (line.StartsWith("detached", StringComparison.Ordinal))
                    {
                        last.IsDetached = true;
                    }
                    else if (line.StartsWith("locked", StringComparison.Ordinal))
                    {
                        last.IsLocked = true;
                    }
                    else if (line.StartsWith("prunable", StringComparison.Ordinal))
                    {
                        last.IsPrunable = true;
                    }
                }
            }

            return worktrees;
        }

        public async Task<bool> AddAsync(string fullpath, string name)
        {
            if (string.IsNullOrEmpty(name))
                Args = $"worktree add {fullpath.Quoted()}";
            else
                Args = $"worktree add -b {name} {fullpath.Quoted()}";

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> AddWithLocalBranchAsync(string fullpath, string localBranch)
        {
            Args = $"worktree add -B {localBranch} {fullpath.Quoted()} {localBranch}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> AddWithRemoteBranchAsync(string fullpath, string name, string remoteBranch, bool tracking)
        {
            var builder = new StringBuilder(1024);
            builder.Append("worktree add ");

            if (tracking)
                builder.Append("--track -b ").Append(string.IsNullOrEmpty(name) ? Path.GetFileName(fullpath) : name).Append(' ');
            else if (!string.IsNullOrEmpty(name))
                builder.Append("-b ").Append(name).Append(' ');

            builder.Append(fullpath.Quoted()).Append(' ').Append(remoteBranch);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> PruneAsync()
        {
            Args = "worktree prune -v";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> LockAsync(string fullpath)
        {
            Args = $"worktree lock {fullpath.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UnlockAsync(string fullpath)
        {
            Args = $"worktree unlock {fullpath.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> RemoveAsync(string fullpath, bool force)
        {
            if (force)
                Args = $"worktree remove -f {fullpath.Quoted()}";
            else
                Args = $"worktree remove {fullpath.Quoted()}";

            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
