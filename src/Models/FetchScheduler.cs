using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal sealed class FetchScheduler
    {
        public static FetchScheduler ForRepository(string commonDir)
        {
            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDir));
            return _repositories.GetOrAdd(key, _ => new FetchScheduler(DateTimeOffset.UtcNow));
        }

        internal FetchScheduler(DateTimeOffset openedAt)
        {
            _openedAt = openedAt;
        }

        public DateTimeOffset? LastSuccess { get { lock (_stateLock) return _lastSuccess; } }
        public DateTimeOffset? LastFailure { get { lock (_stateLock) return _lastFailure; } }

        public DateTimeOffset NextFetchAt(int interval)
        {
            lock (_stateLock)
            {
                if (_lastFailure.HasValue)
                    return _lastFailure.Value.AddMinutes(1);
                return _lastSuccess?.AddMinutes(Math.Clamp(interval, 1, 60)) ?? _openedAt.AddMinutes(1);
            }
        }

        public void MarkFetched(DateTimeOffset now)
        {
            lock (_stateLock)
            {
                _lastSuccess = now;
                _lastFailure = null;
            }
        }

        public void MarkFailed(DateTimeOffset now)
        {
            lock (_stateLock)
                _lastFailure = now;
        }

        public async Task<IDisposable> TryEnterAutomaticAsync(CancellationToken token)
        {
            if (_remoteOperation.CurrentCount == 0 || Interlocked.CompareExchange(ref _automaticPending, 1, 0) != 0)
                return null;

            var entered = false;
            var handedOff = false;
            try
            {
                await _automaticSlots.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                token.ThrowIfCancellationRequested();
                if (_remoteOperation.CurrentCount == 0)
                    return null;

                var lease = new Lease(() =>
                {
                    _automaticSlots.Release();
                    Interlocked.Exchange(ref _automaticPending, 0);
                });
                handedOff = true;
                return lease;
            }
            finally
            {
                if (!handedOff)
                {
                    if (entered)
                        _automaticSlots.Release();
                    Interlocked.Exchange(ref _automaticPending, 0);
                }
            }
        }

        public async Task<IDisposable> EnterRemoteOperationAsync(CancellationToken token)
        {
            await _remoteOperation.WaitAsync(token).ConfigureAwait(false);
            return new Lease(() => _remoteOperation.Release());
        }

        private sealed class Lease(Action release) : IDisposable
        {
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
            private Action _release = release;
        }

        private static readonly ConcurrentDictionary<string, FetchScheduler> _repositories = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private static readonly SemaphoreSlim _automaticSlots = new(4);
        private readonly SemaphoreSlim _remoteOperation = new(1);
        private readonly object _stateLock = new();
        private readonly DateTimeOffset _openedAt;
        private DateTimeOffset? _lastSuccess;
        private DateTimeOffset? _lastFailure;
        private int _automaticPending;
    }
}
