using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Checkout : Command
    {
        public Checkout(string repo, bool lockSubmoduleDiscard = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            _lockSubmoduleDiscard = lockSubmoduleDiscard;
        }

        public async Task<bool> BranchAsync(string branch, bool force)
        {
            if (force && _lockSubmoduleDiscard && !await new CheckSubmoduleOverwrite(WorkingDirectory).IsSafeAsync(branch).ConfigureAwait(false))
                return false;

            var builder = new StringBuilder();
            builder.Append("checkout --progress ");
            if (force)
                builder.Append(_lockSubmoduleDiscard ? "--force --no-recurse-submodules " : "--force --recurse-submodules ");
            builder.Append(branch);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> BranchAsync(string branch, string basedOn, bool force, bool allowOverwrite)
        {
            if (force && _lockSubmoduleDiscard && !await new CheckSubmoduleOverwrite(WorkingDirectory).IsSafeAsync(basedOn).ConfigureAwait(false))
                return false;

            var builder = new StringBuilder();
            builder.Append("checkout --progress ");
            if (force)
                builder.Append(_lockSubmoduleDiscard ? "--force --no-recurse-submodules " : "--force --recurse-submodules ");
            builder.Append(allowOverwrite ? "-B " : "-b ");
            builder.Append(branch);
            builder.Append(" ");
            builder.Append(basedOn);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> CommitAsync(string commitId, bool force)
        {
            if (force && _lockSubmoduleDiscard && !await new CheckSubmoduleOverwrite(WorkingDirectory).IsSafeAsync(commitId).ConfigureAwait(false))
                return false;

            var option = force ? (_lockSubmoduleDiscard ? "--force --no-recurse-submodules" : "--force --recurse-submodules") : string.Empty;
            Args = $"checkout {option} --detach --progress {commitId}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UseTheirsAsync(List<string> files)
        {
            var builder = new StringBuilder();
            builder.Append("checkout --theirs --");
            foreach (var f in files)
                builder.Append(' ').Append(f.Quoted());
            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UseMineAsync(List<string> files)
        {
            var builder = new StringBuilder();
            builder.Append("checkout --ours --");
            foreach (var f in files)
                builder.Append(' ').Append(f.Quoted());

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> FileWithRevisionAsync(string file, string revision)
        {
            Args = $"checkout --no-overlay {revision} -- {file.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> MultipleFilesWithRevisionAsync(List<string> files, string revision)
        {
            var builder = new StringBuilder();
            builder
                .Append("checkout --no-overlay ")
                .Append(revision)
                .Append(" --");

            foreach (var f in files)
                builder.Append(' ').Append(f.Quoted());

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        private readonly bool _lockSubmoduleDiscard;
    }
}
