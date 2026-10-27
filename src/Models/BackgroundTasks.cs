using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum BackgroundTaskStatus { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

    public sealed class BackgroundCloneRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string GitExecutable { get; set; } = string.Empty;
        public string CredentialHelper { get; set; } = string.Empty;
        public string ParentFolder { get; set; } = string.Empty;
        public string Remote { get; set; } = string.Empty;
        public string LocalName { get; set; } = string.Empty;
        public string SSHKey { get; set; } = string.Empty;
        public string ExtraArgs { get; set; } = string.Empty;
        public bool Submodules { get; set; }
        public string GroupId { get; set; } = string.Empty;
        public int Bookmark { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Target => Path.GetFullPath(Path.Combine(ParentFolder, LocalName));
    }

    public sealed class BackgroundTaskState
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = "Clone";
        public string Repository { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public int Bookmark { get; set; }
        public BackgroundTaskStatus Status { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Detail { get; set; } = string.Empty;
        public bool IsActive => Status is BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running;
        public string StatusText => App.Text("Background.Status." + Status);
        public string LocalTime => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    }

    public sealed class BackgroundFetchSettings
    {
        public bool Enabled { get; set; }
        public int Interval { get; set; } = 10;
        public string GitExecutable { get; set; } = string.Empty;
        public string CredentialHelper { get; set; } = string.Empty;
        public List<string> Repositories { get; set; } = new();
    }

    internal static class BackgroundTasks
    {
        internal static string StorageRootOverride { get; set; }
        internal static string ExecutableOverride { get; set; }
        public static string Root => StorageRootOverride ?? Path.Combine(Native.OS.BasicDirectories.ConfigDir, "background");
        public static string WorkerLock => Path.Combine(Root, "worker.lock");
        private static string ControlLock => Path.Combine(Root, "control.lock");

        public static T Read<T>(string file, JsonTypeInfo<T> type)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024)
                    return default;
                return JsonSerializer.Deserialize(File.ReadAllText(file), type);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
        }

        public static void Write<T>(string file, T value, JsonTypeInfo<T> type)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, type));
                File.Move(temporary, file, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void Validate(BackgroundCloneRequest request)
        {
            request.ExtraArgs ??= string.Empty;
            request.SSHKey ??= string.Empty;
            if (!Guid.TryParseExact(request.Id, "N", out _) || !Directory.Exists(request.ParentFolder) ||
                string.IsNullOrWhiteSpace(request.LocalName) || Path.IsPathRooted(request.LocalName) ||
                !request.Target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ParentFolder)) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Remote.IsValidURL(request.Remote) || request.Remote.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                !Path.IsPathRooted(request.GitExecutable) || !File.Exists(request.GitExecutable))
                throw new ArgumentException("Invalid clone destination or remote URL.");
            if (Uri.TryCreate(request.Remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
                (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)))
                throw new ArgumentException("Use your Git credential manager instead of credentials in the URL.");
            if (request.ExtraArgs.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(request.ExtraArgs, "credential|extraheader|password|token|secret", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("Keep authentication in your Git credential manager, not clone arguments.");
        }

        public static async Task<string> EnqueueCloneAsync(BackgroundCloneRequest request)
        {
            Validate(request);
            using var gate = await ProcessFileLock.EnterAsync(ControlLock, CancellationToken.None).ConfigureAwait(false);
            await Task.Run(EnsureStarted).ConfigureAwait(false);
            Write(StatePath(request.Id), new BackgroundTaskState
            {
                Id = request.Id, Repository = request.Target, GroupId = request.GroupId,
                Bookmark = request.Bookmark, Detail = "Clone", Status = BackgroundTaskStatus.Queued,
            }, JsonCodeGen.Default.BackgroundTaskState);
            Write(Path.Combine(Root, "queue", request.Id + ".json"), request, JsonCodeGen.Default.BackgroundCloneRequest);
            return request.Id;
        }

        public static string StatePath(string id) => Path.Combine(Root, "states", id + ".json");

        public static List<BackgroundTaskState> GetStates()
        {
            var dir = Path.Combine(Root, "states");
            if (!Directory.Exists(dir))
                return new();
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(p => Read(p, JsonCodeGen.Default.BackgroundTaskState)).Where(s => s != null && Guid.TryParseExact(s.Id, "N", out _))
                .OrderByDescending(s => s.UpdatedAt).Take(100).ToList();
        }

        public static void Cancel(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _))
                return;
            Directory.CreateDirectory(Path.Combine(Root, "cancel"));
            File.WriteAllText(Path.Combine(Root, "cancel", id), string.Empty);
        }

        public static async Task ConfigureFetchAsync(BackgroundFetchSettings settings, IEnumerable<string> add = null, string remove = null)
        {
            using var gate = await ProcessFileLock.EnterAsync(ControlLock, CancellationToken.None).ConfigureAwait(false);
            var previous = Read(Path.Combine(Root, "fetch.json"), JsonCodeGen.Default.BackgroundFetchSettings);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            settings.Repositories = (previous?.Repositories ?? new List<string>()).Concat(add ?? Array.Empty<string>())
                .Where(p => !comparer.Equals(p, remove)).Distinct(comparer).ToList();
            Write(Path.Combine(Root, "fetch.json"), settings, JsonCodeGen.Default.BackgroundFetchSettings);
            if (settings.Enabled && settings.Repositories.Count > 0)
                await Task.Run(EnsureStarted).ConfigureAwait(false);
        }

        private static void EnsureStarted()
        {
            var active = false;
            using (var probe = ProcessFileLock.TryOpen(WorkerLock))
                if (probe == null)
                    active = true;
            var version = typeof(BackgroundTasks).Assembly.GetName().Version!.ToString();
            if (active && (!File.Exists(Path.Combine(Root, "worker-version")) || File.ReadAllText(Path.Combine(Root, "worker-version")) == version))
                return;
            var executable = ExecutableOverride ?? Environment.ProcessPath;
            if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "SourceGit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run the packaged SourceGit application for background tasks.");
            // A running worker must not lock files which the UI updater replaces.
            var original = new FileInfo(executable);
            var runtime = Path.Combine(Root, "runtime", $"{typeof(BackgroundTasks).Assembly.GetName().Version}-{original.Length}-{original.LastWriteTimeUtc.Ticks}");
            Directory.CreateDirectory(runtime);
            var shadow = Path.Combine(runtime, original.Name);
            if (!File.Exists(Path.Combine(runtime, "ready")))
            {
                foreach (var file in Directory.EnumerateFiles(original.DirectoryName!))
                {
                    var name = Path.GetFileName(file);
                    if (name == original.Name || name == "SourceGit.dll" || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        name is "SourceGit.deps.json" or "SourceGit.runtimeconfig.json")
                        File.Copy(file, Path.Combine(runtime, name), true);
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(shadow, File.GetUnixFileMode(executable));
                File.WriteAllText(Path.Combine(runtime, "ready"), string.Empty);
            }
            if (active)
            {
                Write(Path.Combine(Root, "restart.json"), shadow, JsonCodeGen.Default.String);
                return;
            }
            StartWorker(shadow);
        }

        private static void StartWorker(string executable)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable) };
            start.ArgumentList.Add("--background-worker");
            start.ArgumentList.Add(Root);
            using var worker = Process.Start(start) ?? throw new IOException("Could not start background tasks.");
        }

        public static async Task RunAsync(string root, CancellationToken stop = default)
        {
            StorageRootOverride = Path.GetFullPath(root);
            using var instance = ProcessFileLock.TryOpen(WorkerLock);
            if (instance == null)
                return;
            File.WriteAllText(Path.Combine(Root, "worker-version"), typeof(BackgroundTasks).Assembly.GetName().Version!.ToString());
            File.WriteAllText(Path.Combine(Root, "worker-pid"), Environment.ProcessId.ToString());
            CleanOldRuntimes();
            Directory.CreateDirectory(Path.Combine(Root, "queue"));
            foreach (var state in GetStates().Where(s => s.Status == BackgroundTaskStatus.Running))
            {
                state.Status = BackgroundTaskStatus.Interrupted;
                state.Detail = "The previous worker stopped. Existing files were kept.";
                Write(StatePath(state.Id), state, JsonCodeGen.Default.BackgroundTaskState);
                var oldRequest = Path.Combine(Root, "queue", state.Id + ".json");
                if (File.Exists(oldRequest)) File.Delete(oldRequest);
            }
            var tasks = new Dictionary<string, Task>();
            var fetches = new Dictionary<string, Task>();
            var fetchCancellations = new Dictionary<string, CancellationTokenSource>();
            var due = new Dictionary<string, DateTimeOffset>();
            while (!stop.IsCancellationRequested)
            {
                var settings = Read(Path.Combine(Root, "fetch.json"), JsonCodeGen.Default.BackgroundFetchSettings) ?? new();
                var replacement = Read(Path.Combine(Root, "restart.json"), JsonCodeGen.Default.String);
                foreach (var id in tasks.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList()) tasks.Remove(id);
                foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "queue"), "*.json"))
                {
                    var request = Read(path, JsonCodeGen.Default.BackgroundCloneRequest);
                    if (replacement != null) break;
                    if (request == null || !Guid.TryParseExact(request.Id, "N", out _) || Path.GetFileNameWithoutExtension(path) != request.Id)
                    {
                        File.Move(path, path + ".rejected", true);
                        continue;
                    }
                    if (tasks.ContainsKey(request.Id) || tasks.Count >= 2)
                        continue;
                    try { Validate(request); }
                    catch (Exception ex)
                    {
                        Write(StatePath(request.Id), new BackgroundTaskState { Id = request.Id, Repository = request.ParentFolder,
                            Status = BackgroundTaskStatus.Failed, Detail = ex.Message }, JsonCodeGen.Default.BackgroundTaskState);
                        File.Move(path, path + ".rejected", true);
                        continue;
                    }
                    tasks.Add(request.Id, RunCloneAsync(request, path, stop));
                }
                foreach (var path in fetches.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
                {
                    fetches.Remove(path);
                    fetchCancellations[path].Dispose();
                    fetchCancellations.Remove(path);
                }
                foreach (var item in fetchCancellations)
                    if (!settings.Enabled || !settings.Repositories.Contains(item.Key)) item.Value.Cancel();
                if (replacement == null && settings.Enabled && !string.IsNullOrEmpty(settings.GitExecutable))
                {
                    Native.OS.GitExecutable = settings.GitExecutable;
                    Native.OS.CredentialHelper = settings.CredentialHelper;
                    foreach (var repository in settings.Repositories.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                    {
                        if (!due.TryGetValue(repository, out var next)) due[repository] = DateTimeOffset.UtcNow.AddMinutes(1);
                        else if (!fetches.ContainsKey(repository) && next <= DateTimeOffset.UtcNow)
                        {
                            due[repository] = DateTimeOffset.UtcNow.AddMinutes(1);
                            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
                            fetchCancellations.Add(repository, cancellation);
                            fetches.Add(repository, RunFetchAsync(repository, settings.Interval, cancellation.Token));
                        }
                    }
                }
                if (replacement != null && tasks.Count == 0 && fetches.Count == 0)
                {
                    using var control = await ProcessFileLock.EnterAsync(ControlLock, stop).ConfigureAwait(false);
                    var runtimeRoot = Path.GetFullPath(Path.Combine(Root, "runtime")) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(replacement).StartsWith(runtimeRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                        !File.Exists(replacement))
                        throw new IOException("Invalid background runtime replacement.");
                    File.Delete(Path.Combine(Root, "restart.json"));
                    instance.Dispose();
                    StartWorker(replacement);
                    return;
                }
                if (tasks.Count == 0 && fetches.Count == 0 && (!settings.Enabled || settings.Repositories.Count == 0))
                {
                    using var control = await ProcessFileLock.EnterAsync(ControlLock, stop).ConfigureAwait(false);
                    var latest = Read(Path.Combine(Root, "fetch.json"), JsonCodeGen.Default.BackgroundFetchSettings);
                    if (!Directory.EnumerateFiles(Path.Combine(Root, "queue"), "*.json").Any() &&
                        (latest == null || !latest.Enabled || latest.Repositories.Count == 0))
                    {
                        instance.Dispose(); // Release under the same gate used when clients queue and start work.
                        return;
                    }
                }
                await Task.Delay(2000, stop).ConfigureAwait(false);
            }
            await Task.WhenAll(tasks.Values.Concat(fetches.Values)).ConfigureAwait(false);
        }

        private static async Task RunCloneAsync(BackgroundCloneRequest request, string queuedFile, CancellationToken stop)
        {
            var state = Read(StatePath(request.Id), JsonCodeGen.Default.BackgroundTaskState) ?? new BackgroundTaskState { Id = request.Id, Repository = request.Target };
            state.Id = request.Id;
            state.Repository = request.Target;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
            if (File.Exists(Path.Combine(Root, "cancel", request.Id))) cancel.Cancel();
            using var timer = new Timer(_ => CancelIfRequested(Path.Combine(Root, "cancel", request.Id), cancel), null, 0, 250);
            try
            {
                Validate(request);
                Native.OS.GitExecutable = request.GitExecutable;
                Native.OS.CredentialHelper = request.CredentialHelper;
                state.Status = BackgroundTaskStatus.Running;
                Write(StatePath(state.Id), state, JsonCodeGen.Default.BackgroundTaskState);
                var log = new TaskLog(state);
                var clone = new Commands.Clone(string.Empty, request.ParentFolder, request.Remote, request.LocalName, request.SSHKey, request.ExtraArgs)
                { RaiseError = false, AllowInteractiveAuthentication = false, CancellationToken = cancel.Token, Log = log };
                if (!await clone.ExecAsync().ConfigureAwait(false))
                    throw new IOException("Clone failed. Check credentials, network and available space. " + state.Detail);
                if (!string.IsNullOrEmpty(request.SSHKey))
                    if (!await new Commands.Config(request.Target).SetAsync("remote.origin.sshkey", request.SSHKey).ConfigureAwait(false))
                        throw new IOException("Could not save the repository SSH key path.");
                if (request.Submodules)
                {
                    var command = new Commands.Submodule(request.Target)
                    { RaiseError = false, AllowInteractiveAuthentication = false, SSHKey = request.SSHKey, CancellationToken = cancel.Token, Log = log };
                    if (!await command.UpdateAsync(new List<string>(), true, true, false).ConfigureAwait(false))
                        throw new IOException("Submodule update failed. The clone was kept. " + state.Detail);
                }
                state.Status = BackgroundTaskStatus.Succeeded;
                state.Detail = string.Empty;
            }
            catch (Exception ex)
            {
                state.Status = cancel.IsCancellationRequested ? BackgroundTaskStatus.Cancelled : BackgroundTaskStatus.Failed;
                state.Detail = Redact(ex.Message);
            }
            finally
            {
                state.UpdatedAt = DateTimeOffset.UtcNow;
                Write(StatePath(state.Id), state, JsonCodeGen.Default.BackgroundTaskState);
                if (File.Exists(queuedFile)) File.Delete(queuedFile);
                var marker = Path.Combine(Root, "cancel", request.Id);
                if (File.Exists(marker)) File.Delete(marker);
            }
        }

        internal static async Task RunFetchAsync(string repository, int interval, CancellationToken stopping)
        {
            BackgroundTaskState state = null;
            string cancelPath = null;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var token = cancellation.Token;
            try
            {
                if (!Directory.Exists(repository)) return;
                var common = await new Commands.QueryGitCommonDir(repository).GetResultAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(common)) return;
                var scheduler = FetchScheduler.ForRepository(common);
                var id = FetchId(common);
                cancelPath = Path.Combine(Root, "cancel", id);
                using var monitor = new Timer(_ => CancelIfRequested(cancelPath, cancellation), null, 0, 250);
                var busyFile = ProcessFileLock.RepositoryPath(common) + ".busy";
                if (File.Exists(busyFile) && long.TryParse(File.ReadAllText(busyFile), out var until) && until > DateTimeOffset.UtcNow.UtcTicks) return;
                var previous = Read(StatePath(id), JsonCodeGen.Default.BackgroundTaskState);
                if (previous != null && previous.UpdatedAt.AddMinutes(previous.Status == BackgroundTaskStatus.Failed ? 1 : Math.Clamp(interval, 1, 60)) > DateTimeOffset.UtcNow)
                    return;
                var nextFetchAt = scheduler.NextFetchAt(interval);
                if (scheduler.LastSuccess.HasValue && nextFetchAt > DateTimeOffset.UtcNow) return;
                state = new BackgroundTaskState { Id = id, Kind = "Fetch", Repository = repository, Status = BackgroundTaskStatus.Running };
                var gitDir = new Commands.QueryGitDir(repository).GetResult();
                if (File.Exists(Path.Combine(gitDir, "index.lock"))) return;
                using var slot = await scheduler.TryEnterAutomaticAsync(token).ConfigureAwait(false);
                if (slot == null) return;
                var remotes = await new Commands.QueryRemotes(repository).GetResultAsync().ConfigureAwait(false);
                if (!remotes.Any(r => !r.DisableAutoFetch)) return;
                Write(StatePath(id), state, JsonCodeGen.Default.BackgroundTaskState);
                var success = true;
                foreach (var remote in remotes.Where(r => !r.DisableAutoFetch))
                    success &= await new Commands.Fetch(repository, remote).WithCancellation(token).Use(new TaskLog(state)).ExecAsync().ConfigureAwait(false);
                state.Status = token.IsCancellationRequested ? BackgroundTaskStatus.Cancelled : success ? BackgroundTaskStatus.Succeeded : BackgroundTaskStatus.Failed;
                state.UpdatedAt = DateTimeOffset.UtcNow;
                if (success) state.Detail = string.Empty;
                Write(StatePath(id), state, JsonCodeGen.Default.BackgroundTaskState);
                if (success) scheduler.MarkFetched(state.UpdatedAt);
            }
            catch (OperationCanceledException)
            {
                if (state != null)
                {
                    state.Status = BackgroundTaskStatus.Cancelled;
                    state.UpdatedAt = DateTimeOffset.UtcNow;
                    Write(StatePath(state.Id), state, JsonCodeGen.Default.BackgroundTaskState);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Native.OS.LogException(ex);
            }
            finally { if (cancelPath != null && File.Exists(cancelPath)) File.Delete(cancelPath); }
        }

        private sealed class TaskLog(BackgroundTaskState state) : ICommandLog
        {
            private DateTimeOffset _lastWrite;
            public void AppendLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("$ git", StringComparison.Ordinal)) return;
                lock (state)
                {
                    state.Detail = Redact(line);
                    if (DateTimeOffset.UtcNow - _lastWrite < TimeSpan.FromSeconds(1)) return;
                    _lastWrite = DateTimeOffset.UtcNow;
                    Write(StatePath(state.Id), state, JsonCodeGen.Default.BackgroundTaskState);
                }
            }
        }

        internal static string FetchId(string commonDir)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDir));
            if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
        }

        public static void SetRepositoryBusy(string commonDir, bool busy)
        {
            if (string.IsNullOrEmpty(commonDir)) return;
            var path = ProcessFileLock.RepositoryPath(commonDir) + ".busy";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (busy) File.WriteAllText(path, DateTimeOffset.UtcNow.AddSeconds(6).UtcTicks.ToString());
            else if (File.Exists(path)) File.Delete(path);
        }

        private static string Redact(string text)
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(text, @"(https?://)[^/\s@]+@", "$1***@");
            return clean.Length > 2000 ? clean[..2000] : clean;
        }

        private static void CancelIfRequested(string path, CancellationTokenSource cancellation)
        {
            try { if (File.Exists(path)) cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private static void CleanOldRuntimes()
        {
            var root = Path.GetFullPath(Path.Combine(Root, "runtime"));
            if (!Directory.Exists(root)) return;
            var current = Path.GetDirectoryName(Environment.ProcessPath);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(directory, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(directory).Any()) continue;
                var files = Directory.GetFiles(directory);
                if (files.Any(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                    !(Path.GetFileName(file) is "ready" or "SourceGit.exe" or "SourceGit" or "sourcegit" or "SourceGit.deps.json" or "SourceGit.runtimeconfig.json" ||
                        file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))) continue;
                try
                {
                    foreach (var file in files) File.Delete(file);
                    Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
