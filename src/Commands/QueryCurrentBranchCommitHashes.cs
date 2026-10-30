using System.Collections.Generic;
using System.Diagnostics;
using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCurrentBranchCommitHashes : Command
    {
        public QueryCurrentBranchCommitHashes(string repo, ulong sinceTimestamp)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"log --since=@{sinceTimestamp} --format=%H";
        }

        public async Task<HashSet<string>> GetResultAsync()
        {
            var outs = new HashSet<string>();

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

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 8 } line)
                    outs.Add(line);

                await proc.WaitForExitAsync().ConfigureAwait(false);
                await error.ConfigureAwait(false);
                CancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Ignore exceptions;
            }

            return outs;
        }
    }
}
