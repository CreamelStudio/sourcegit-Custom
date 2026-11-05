using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class Discard
    {
        /// <summary>
        ///     Discard all local changes (unstaged & staged)
        /// </summary>
        public static async Task<bool> AllAsync(string repo, bool includeModified, bool includeUntracked, bool includeIgnored, Models.ICommandLog log, bool lockSubmoduleDiscard = true)
        {
            if (includeModified && lockSubmoduleDiscard && !await new CheckSubmoduleOverwrite(repo).IsSafeAsync("HEAD").ConfigureAwait(false))
                return false;

            if (includeUntracked)
            {
                // Untracked paths that contains `.git` file (detached submodule) must be removed manually.
                var changes = await new QueryLocalChanges(repo).GetResultAsync().ConfigureAwait(false);
                try
                {
                    foreach (var c in changes)
                    {
                        if (c.WorkTree == Models.ChangeState.Untracked ||
                            c.WorkTree == Models.ChangeState.Added ||
                            c.Index == Models.ChangeState.Added ||
                            c.Index == Models.ChangeState.Renamed)
                        {
                            var fullPath = Path.Combine(repo, c.Path);
                            if (Directory.Exists(fullPath) && (!lockSubmoduleDiscard || !IsNestedRepository(fullPath)))
                                Directory.Delete(fullPath, true);
                        }
                    }
                }
                catch (Exception e)
                {
                    Models.Notification.Send(repo, $"Failed to discard changes. Reason: {e.Message}", true);
                }

                if (includeIgnored)
                    await new Clean(repo, Models.CleanMode.UntrackedAndIgnoredFiles).Use(log).ExecAsync().ConfigureAwait(false);
                else
                    await new Clean(repo, Models.CleanMode.OnlyUntrackedFiles).Use(log).ExecAsync().ConfigureAwait(false);
            }
            else if (includeIgnored)
            {
                await new Clean(repo, Models.CleanMode.OnlyIgnoredFiles).Use(log).ExecAsync().ConfigureAwait(false);
            }

            if (includeModified)
            {
                var recurse = lockSubmoduleDiscard ? "--no-recurse-submodules" : "--recurse-submodules";
                return await new Reset(repo, "", $"--hard {recurse}").Use(log).ExecAsync().ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>
        ///     Discard selected changes (only unstaged).
        /// </summary>
        /// <param name="repo"></param>
        /// <param name="changes"></param>
        /// <param name="log"></param>
        public static async Task ChangesAsync(string repo, List<Models.Change> changes, Models.ICommandLog log, bool lockSubmoduleDiscard = true)
        {
            var restores = new List<string>();

            try
            {
                foreach (var c in changes)
                {
                    var fullPath = Path.Combine(repo, c.Path);
                    if (lockSubmoduleDiscard && IsNestedRepository(fullPath))
                        continue;

                    if (c.WorkTree == Models.ChangeState.Untracked || c.WorkTree == Models.ChangeState.Added)
                    {
                        if (Directory.Exists(fullPath))
                            Directory.Delete(fullPath, true);
                        else
                            File.Delete(fullPath);
                    }
                    else
                    {
                        restores.Add(c.Path);
                    }
                }
            }
            catch (Exception e)
            {
                Models.Notification.Send(repo, $"Failed to discard changes. Reason: {e.Message}", true);
            }

            if (restores.Count > 0)
            {
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(pathSpecFile, restores).ConfigureAwait(false);
                await new Restore(repo, pathSpecFile, lockSubmoduleDiscard).Use(log).ExecAsync().ConfigureAwait(false);
                File.Delete(pathSpecFile);
            }
        }

        internal static bool IsNestedRepository(string path) =>
            File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"));
    }
}
