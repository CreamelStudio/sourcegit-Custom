using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class CheckSubmoduleOverwrite : Command
    {
        public CheckSubmoduleOverwrite(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> IsSafeAsync(string revision)
        {
            var modules = new HashSet<string>();
            Args = "ls-files --stage -z";
            var index = await ReadToEndAsync().ConfigureAwait(false);
            if (!index.IsSuccess)
            {
                RaiseException(index.StdErr);
                return false;
            }

            foreach (var entry in index.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.StartsWith("160000 ", StringComparison.Ordinal))
                {
                    var path = entry.Substring(entry.IndexOf('\t') + 1);
                    if (Directory.Exists(Path.Combine(WorkingDirectory, path)))
                        modules.Add(path);
                }
            }

            var changes = await new QueryLocalChanges(WorkingDirectory).GetResultAsync().ConfigureAwait(false);
            foreach (var change in changes)
            {
                if (Discard.IsNestedRepository(Path.Combine(WorkingDirectory, change.Path)))
                    modules.Add(change.Path.TrimEnd('/'));
            }

            if (modules.Count == 0)
                return true;

            Args = $"ls-tree -rz {revision.Quoted()}";
            var target = await ReadToEndAsync().ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                RaiseException(target.StdErr);
                return false;
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Even --no-recurse-submodules can delete a module replaced by a regular file.
            foreach (var entry in target.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = entry.Substring(entry.IndexOf('\t') + 1);
                foreach (var module in modules)
                {
                    if (path.Equals(module, comparison) && entry.StartsWith("160000 ", StringComparison.Ordinal))
                        continue;
                    if (path.Equals(module, comparison) || path.StartsWith(module + "/", comparison) || module.StartsWith(path + "/", comparison))
                    {
                        RaiseException($"Submodule discard is locked. This operation would overwrite '{module}'. Unlock submodule discard to continue.");
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
