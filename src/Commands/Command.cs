using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Args { get; set; } = string.Empty;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public bool AllowInteractiveAuthentication { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            if (CancellationToken.IsCancellationRequested)
                return false;

            if (this is Fetch or Pull or Push)
            {
                var commonDir = await new QueryGitCommonDir(WorkingDirectory).GetResultAsync().ConfigureAwait(false);
                var scheduler = Models.FetchScheduler.ForRepository(string.IsNullOrEmpty(commonDir) ? WorkingDirectory : commonDir);
                try
                {
                    using var operation = await scheduler.EnterRemoteOperationAsync(CancellationToken).ConfigureAwait(false);
                    return await ExecCoreAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return await ExecCoreAsync().ConfigureAwait(false);
        }

        private async Task<bool> ExecCoreAsync()
        {
            if (CancellationToken.IsCancellationRequested)
                return false;

            if (this is Checkout or Branch || this is Worktree && Args.StartsWith("worktree add ", StringComparison.Ordinal))
            {
                try
                {
                    await new Worktree(WorkingDirectory) { Log = Log, CancellationToken = CancellationToken }
                        .ReadAllAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return false; }
            }

            if (this is Checkout or Rebase or InteractiveRebase or Merge or CherryPick or Revert or Pull ||
                this is Reset && !Args.Contains("--pathspec-from-file", StringComparison.Ordinal))
            {
                try { await Models.RecoveryTimeline.CaptureAsync(WorkingDirectory, GetType().Name.ToLowerInvariant()).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (RaiseError)
                        RaiseException($"Could not preserve the recovery commit: {ex.Message}");
                    return false;
                }
            }

            Log?.AppendLine($"$ git {Args}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            var captured = new CapturedProcess() { Process = proc };
            var capturedLock = new object();
            try
            {
                lock (capturedLock)
                {
                    if (CancellationToken.IsCancellationRequested)
                        return false;
                    proc.Start();
                }
            }
            catch (Exception e)
            {
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            // Only use cancellation for commands which can safely be interrupted.
            using var registration = CancellationToken.Register(() =>
            {
                lock (capturedLock)
                {
                    if (captured is { Process: { HasExited: false } })
                        Native.OS.TerminateProcess(captured.Process);
                }
            });

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                // Cancellation terminates the process; drain its output before releasing the repository lock.
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HandleOutput(e.Message, errs);
            }

            lock (capturedLock)
            {
                captured.Process = null;
            }

            Log?.AppendLine(string.Empty);

            if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
            {
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        RaiseException(errMsg);
                }

                return false;
            }

            return !CancellationToken.IsCancellationRequested;
        }

        protected Result ReadToEnd()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = proc.StandardOutput.ReadToEnd();
            rs.StdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected async Task<Result> ReadToEndAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = await proc.StandardOutput.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            rs.StdErr = await proc.StandardError.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var useSetSid = CancellationToken.CanBeCanceled && Native.OS.SupportSetSid();
            var selfExecFile = Environment.ProcessPath;
            var builder = new StringBuilder(2048);

            if (useSetSid)
                builder.Append(Native.OS.GitExecutable.Quoted()).Append(' ');

            builder
                .Append("--no-pager -c core.quotepath=off -c credential.helper=")
                .Append(Native.OS.CredentialHelper)
                .Append(' ');

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);

            var start = new ProcessStartInfo();
            start.FileName = useSetSid ? Native.OS.GetSetSidExecutable() : Native.OS.GitExecutable;
            start.Arguments = builder.ToString();
            start.UseShellExecute = false;
            start.CreateNoWindow = true;

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Force using this app as SSH askpass program
            start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`
            start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
            start.Environment.Add("SOURCEGIT_LAUNCH_AS_ASKPASS", "TRUE");
            if (!OperatingSystem.IsLinux())
                start.Environment.Add("DISPLAY", "required");

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{SSHKey}' -o AddKeysToAgent=yes");

            if (!AllowInteractiveAuthentication)
            {
                start.Environment["GIT_ASKPASS"] = string.Empty;
                start.Environment["GIT_TERMINAL_PROMPT"] = "0";
                start.Environment["GCM_INTERACTIVE"] = "0";
                start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
                if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !start.Environment.ContainsKey("GIT_SSH"))
                    start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
                else if (!string.IsNullOrEmpty(SSHKey) && !Environment.GetEnvironmentVariables().Contains("GIT_SSH_COMMAND"))
                    start.Environment["GIT_SSH_COMMAND"] += " -o BatchMode=yes";
            }

            // Force the C locale on Unix, so that git's output (including fatal errors) can always be parsed.
            if (!OperatingSystem.IsWindows())
            {
                start.Environment.Add("LANG", "C");
                start.Environment.Add("LC_ALL", "C");
            }

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            lock (errs)
                errs.Add(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
