using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Native;

internal static class WindowTitleChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Check(WindowsProjectWindowTitle.MatchesTarget("Server2 [D:\\worktree\\Server2] - IDE", "d:/worktree/Server2"), "Match the full target path across slash styles and case.");
        Check(!WindowsProjectWindowTitle.MatchesTarget("D:/worktree/Server20 - IDE", "D:/worktree/Server2"), "Never confuse a similarly named project with this target.");
        Check(!WindowsProjectWindowTitle.MatchesTarget("D:/worktree/Server2 other - IDE", "D:/worktree/Server2"), "A longer path containing a space is still a different project.");
        Check(!WindowsProjectWindowTitle.MatchesTarget("Server2 - IDE", "D:/worktree/Server2"), "A project name alone cannot identify the worktree of a reused IDE window.");
        Check(WindowsProjectWindowTitle.RemovePrefix("[old] [Debug] Editor", "[old] ") == "[Debug] Editor", "Only remove the prefix owned by SourceGit, preserving the application's bracketed title.");

        var branch = "feature/한글";
        var first = StartInfo("First editor");
        var second = StartInfo("Second editor");
        using var firstTitle = new WindowsProjectWindowTitle(first, () => Volatile.Read(ref branch), false);
        using var secondTitle = new WindowsProjectWindowTitle(second, () => "release", false);
        var firstWindow = await FindWindowAsync("[feature/한글] First editor");
        var secondWindow = await FindWindowAsync("[release] Second editor");

        Volatile.Write(ref branch, "fix/title");
        await WaitForTitleAsync(firstWindow, "[fix/title] First editor");
        Check(WindowsProjectWindowTitle.ReadTitle(secondWindow) == "[release] Second editor", "Different worktrees keep independent branch labels.");

        Check(WindowsProjectWindowTitle.SetTitle(firstWindow, "Changed scene - Editor"), "The test editor can replace its title.");
        await WaitForTitleAsync(firstWindow, "[fix/title] Changed scene - Editor");
        Check(WindowsProjectWindowTitle.SetTitle(firstWindow, "[fix/title] Changed scene - Editor *"), "The test editor can append an unsaved marker.");
        Volatile.Write(ref branch, "next");
        await WaitForTitleAsync(firstWindow, "[next] Changed scene - Editor *");

        Volatile.Write(ref branch, string.Empty);
        await WaitForTitleAsync(firstWindow, "Changed scene - Editor *");
        Volatile.Write(ref branch, "next");
        await WaitForTitleAsync(firstWindow, "[next] Changed scene - Editor *");
        var project = Path.Combine(Path.GetTempPath(), "SourceGit.TitleCheck-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(project, "Library");
        Directory.CreateDirectory(library);
        var instanceFile = Path.Combine(library, "EditorInstance.json");
        try
        {
            GetWindowThreadProcessId(firstWindow, out var processId);
            File.WriteAllText(instanceFile, "{\"process_id\":" + processId + ",}");
            var reopen = StartInfo(project);
            reopen.WorkingDirectory = project;
            using var replacement = new WindowsProjectWindowTitle(reopen, () => "replacement", true);
            await WaitForTitleAsync(firstWindow, "[replacement] Changed scene - Editor *");
            firstTitle.Dispose();
            firstTitle.Dispose();
            Check(WindowsProjectWindowTitle.ReadTitle(firstWindow) == "[replacement] Changed scene - Editor *", "Reopening the same window transfers ownership without duplicate labels or the older monitor removing the new branch.");
            replacement.Dispose();
            Check(WindowsProjectWindowTitle.ReadTitle(firstWindow) == "Changed scene - Editor *", "Closing a repository restores the latest application title and repeated disposal is safe.");
        }
        finally
        {
            File.Delete(instanceFile);
            Directory.Delete(library);
            Directory.Delete(project);
        }

        var ideTitle = $"IDE [{project}]";
        Check(WindowsProjectWindowTitle.SetTitle(firstWindow, ideTitle), "Expose a reused IDE window's full project path.");
        var forwarded = StartInfo(project);
        forwarded.ArgumentList[^2] = "--title-test-no-window";
        using (var reusedIDE = new WindowsProjectWindowTitle(forwarded, () => "ide-branch", false))
        {
            await WaitForTitleAsync(firstWindow, "[ide-branch] " + ideTitle);
            Check(WindowsProjectWindowTitle.ReadTitle(secondWindow) == "[release] Second editor", "An exited IDE launcher reconnects only to the exact target's window.");
        }
        Check(WindowsProjectWindowTitle.ReadTitle(firstWindow) == ideTitle, "Reused IDE windows also restore their original title.");

        PostMessage(firstWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);
        PostMessage(secondWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);
        var exitWait = Stopwatch.StartNew();
        while (!secondTitle.IsDisposed && exitWait.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        Check(secondTitle.IsDisposed, "Closing the application releases its monitor instead of polling for the rest of the repository's lifetime.");
        Console.WriteLine("Window title checks passed: two real Windows processes, independent branches, Unicode, branch switches, scene/title resets, unsaved markers, missing branches, Unity/forwarded-IDE reattachment, repeated opens, title restoration and monitor cleanup.");
    }

    private static ProcessStartInfo StartInfo(string title)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WindowTitleChecks).Assembly.Location);
        start.ArgumentList.Add("--title-test-window");
        start.ArgumentList.Add(title);
        return start;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IntPtr> FindWindowAsync(string title)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var window = FindWindow(null, title);
            if (window != IntPtr.Zero)
                return window;
            await Task.Delay(50);
        }
        throw new Exception($"Timed out waiting for the test window: {title}");
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForTitleAsync(IntPtr window, string title)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (WindowsProjectWindowTitle.ReadTitle(window) == title)
                return;
            await Task.Delay(50);
        }
        throw new Exception($"Expected '{title}', observed '{WindowsProjectWindowTitle.ReadTitle(window)}'.");
    }

    [SupportedOSPlatform("windows")]
    public static void RunWindow(string title)
    {
        var window = CreateWindowEx(0, "STATIC", title, 0x10CF0000, -30000, -30000, 400, 120, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Check(window != IntPtr.Zero, "Create a real native test window.");
        var lifetime = Stopwatch.StartNew();
        try
        {
            while (IsWindow(window) && lifetime.Elapsed < TimeSpan.FromSeconds(45))
            {
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                Thread.Sleep(10);
            }
        }
        finally { DestroyWindow(window); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint ID;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);
}
