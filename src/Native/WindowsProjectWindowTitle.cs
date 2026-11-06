using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SourceGit.Native
{
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsProjectWindowTitle : IDisposable
    {
        public WindowsProjectWindowTitle(ProcessStartInfo start, Func<string> getBranch, bool isUnity)
        {
            _executable = Path.GetFullPath(start.FileName);
            _target = start.ArgumentList[^1];
            _instanceFile = isUnity ? Path.Combine(start.WorkingDirectory, "Library", "EditorInstance.json") : null;
            _getBranch = getBranch;
            _process = ReadUnityInstance();
            if (_process != null)
            {
                ShowWindowAsync(_process.MainWindowHandle, 9);
                SetForegroundWindow(_process.MainWindowHandle);
            }
            else
                _process = Process.Start(start) ?? throw new InvalidOperationException("The project program did not start.");
            _timer = new Timer(_ => Update(), null, 1000, 1000);
        }

        public bool IsDisposed => Volatile.Read(ref _disposed);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _timer.Dispose();
                RestoreTitle();
                _process.Dispose();
            }
        }

        private void Update()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                try
                {
                    var window = FindWindow();
                    var previousWindow = _window;
                    if (_window != window)
                    {
                        RestoreTitle();
                        _window = window;
                    }
                    if (_window == IntPtr.Zero)
                    {
                        if (_process.HasExited && (previousWindow != IntPtr.Zero || _started.Elapsed > TimeSpan.FromMinutes(1)))
                            Dispose();
                        return;
                    }
                    lock (Titles)
                    {
                        GetWindowThreadProcessId(_window, out var processId);
                        Titles.TryGetValue(_window, out var previous);
                        if (previous.Process == processId && previous.Owner > _order)
                        {
                            Dispose();
                            return;
                        }
                        var current = ReadTitle(_window);
                        if (current.Length == 0)
                            return;
                        var original = RemovePrefix(current, previous.Process == processId ? previous.Prefix : null);
                        var branch = _getBranch();
                        var prefix = string.IsNullOrWhiteSpace(branch) ? string.Empty : $"[{branch}] ";
                        if (current == prefix + original || SetTitle(_window, prefix + original))
                            Titles[_window] = (_order, processId, prefix);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or JsonException)
                {
                    // A project window or its Unity instance file may disappear during startup/shutdown.
                }
            }
        }

        private IntPtr FindWindow()
        {
            if (!_process.HasExited)
            {
                _process.Refresh();
                var window = _process.MainWindowHandle;
                if (window != IntPtr.Zero)
                    return window;
            }

            using (var editor = ReadUnityInstance())
            {
                if (editor != null)
                    return editor.MainWindowHandle;
            }

            // shortcut: reused IDE windows need a full target path in their title; add app-specific discovery if needed.
            var found = IntPtr.Zero;
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_executable));
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!MatchesExecutable(process))
                            continue;
                        EnumWindows((window, _) =>
                        {
                            GetWindowThreadProcessId(window, out var owner);
                            if (owner == process.Id && IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero && MatchesTarget(ReadTitle(window), _target))
                                found = window;
                            return found == IntPtr.Zero;
                        }, IntPtr.Zero);
                        if (found != IntPtr.Zero)
                            break;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        // Other instances may exit or run with different privileges.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
            return found;
        }

        private bool MatchesExecutable(Process process) =>
            string.Equals(process.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase);

        private Process ReadUnityInstance()
        {
            Process editor = null;
            try
            {
                if (_instanceFile == null || !File.Exists(_instanceFile))
                    return null;
                using var instance = JsonDocument.Parse(File.ReadAllText(_instanceFile), new JsonDocumentOptions { AllowTrailingCommas = true });
                if (instance.RootElement.TryGetProperty("process_id", out var id) && id.TryGetInt32(out var processId) && processId > 0)
                {
                    editor = Process.GetProcessById(processId);
                    if (MatchesExecutable(editor))
                        return editor;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or JsonException)
            {
                // Missing, stale or unreadable instance information must not prevent launching Unity.
            }
            editor?.Dispose();
            return null;
        }

        internal static bool MatchesTarget(string title, string target)
        {
            if (!Path.IsPathFullyQualified(target))
                return false;
            var text = title.Replace('\\', '/');
            var path = target.Replace('\\', '/').TrimEnd('/');
            var index = text.IndexOf(path, StringComparison.OrdinalIgnoreCase);
            var end = index + path.Length;
            return index >= 0 && (end == text.Length || "\"')]".Contains(text[end]) || text[end..].StartsWith(" - ", StringComparison.Ordinal));
        }

        internal static string RemovePrefix(string title, string prefix) =>
            !string.IsNullOrEmpty(prefix) && title.StartsWith(prefix, StringComparison.Ordinal) ? title[prefix.Length..] : title;

        private void RestoreTitle()
        {
            lock (Titles)
            {
                if (Titles.TryGetValue(_window, out var previous) && previous.Owner == _order)
                {
                    GetWindowThreadProcessId(_window, out var processId);
                    var title = ReadTitle(_window);
                    if (previous.Process == processId && !string.IsNullOrEmpty(previous.Prefix) && title.StartsWith(previous.Prefix, StringComparison.Ordinal))
                        SetTitle(_window, title[previous.Prefix.Length..]);
                    Titles.Remove(_window);
                }
            }
            _window = IntPtr.Zero;
        }

        internal static string ReadTitle(IntPtr window)
        {
            var text = new StringBuilder(4096);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        internal static bool SetTitle(IntPtr window, string title) =>
            SendMessageTimeout(window, 0x000C, UIntPtr.Zero, title, 0x0002, 200, out var result) != IntPtr.Zero && result != UIntPtr.Zero;

        private static readonly Dictionary<IntPtr, (long Owner, uint Process, string Prefix)> Titles = [];
        private static long _nextOrder;
        private readonly long _order = Interlocked.Increment(ref _nextOrder);
        private readonly object _gate = new();
        private readonly Stopwatch _started = Stopwatch.StartNew();
        private readonly string _executable;
        private readonly string _target;
        private readonly string _instanceFile;
        private readonly Func<string> _getBranch;
        private readonly Process _process;
        private readonly Timer _timer;
        private IntPtr _window;
        private bool _disposed;

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, string text, uint flags, uint timeout, out UIntPtr result);
    }
}
