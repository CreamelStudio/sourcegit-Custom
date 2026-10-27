using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal static class GitQuery
    {
        internal sealed record Result(int ExitCode, string Output, string Error, bool Truncated);

        public static async Task<Result> ReadAsync(string repository, IEnumerable<string> arguments,
            CancellationToken cancellation = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(Native.OS.GitExecutable)
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "--no-pager", "-c", "core.quotepath=false" })
                process.StartInfo.ArgumentList.Add(arg);
            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            process.Start();
            using var registration = timeout.Token.Register(() => Stop(process));
            var output = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024, process);
            var error = ReadBoundedAsync(process.StandardError, 8192, process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("Git query timed out.");
            return new Result(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
        }

        private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int limit, Process process)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var remaining = limit - text.Length;
                text.Append(buffer, 0, Math.Min(remaining, count));
                if (count > remaining)
                {
                    Stop(process);
                    return (text.ToString(), true);
                }
            }
            return (text.ToString(), false);
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
