using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SourceGit.Native;

internal static class ProgramIconChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var pixels = WindowsProgramIcon.ReadPixels(executable);
        Check(pixels is { Length: WindowsProgramIcon.Size * WindowsProgramIcon.Size * 4 } &&
            pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0), "Read an installed executable's visible icon pixels without running it.");
        Check(WindowsProgramIcon.ReadPixels(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")) == null,
            "A missing program has no icon and keeps the generic fallback.");
        var empty = Path.Combine(Path.GetTempPath(), "SourceGit.IconCheck-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(empty, "This file must never be executed.");
            Check(WindowsProgramIcon.ReadPixels(empty) == null, "A file without PE icon resources must not be executed or treated as a logo.");
        }
        finally { File.Delete(empty); }
        using var process = Process.GetCurrentProcess();
        var gdi = GetGuiResources(process.Handle, 0);
        var user = GetGuiResources(process.Handle, 1);
        for (var i = 0; i < 64; i++)
            Check(WindowsProgramIcon.ReadPixels(executable) != null, "Repeated extraction succeeds.");
        Check(GetGuiResources(process.Handle, 0) <= gdi + 2 && GetGuiResources(process.Handle, 1) <= user + 2,
            "Repeated extraction releases GDI and icon handles.");
        Avalonia.Skia.SkiaPlatform.Initialize();
        var bitmap = WindowsProgramIcon.Read(executable);
        Check(bitmap != null && bitmap.PixelSize.Width == WindowsProgramIcon.Size && bitmap.PixelSize.Height == WindowsProgramIcon.Size,
            "Convert the real executable icon into an Avalonia bitmap without creating a window.");
        Check(ReferenceEquals(bitmap, WindowsProgramIcon.Read(executable)), "Reuse the icon for an unchanged executable.");
        var tool = new SourceGit.Models.ExternalTool("Installed program", executable, null, true);
        Check(ReferenceEquals(tool.IconImage, bitmap), "External tools use the executable's icon instead of a bundled logo.");
        using var image = new MemoryStream();
        bitmap.Save(image);
        Check(image.Length > 8 && image.ToArray().Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "The extracted icon remains a valid encoded PNG after releasing native handles and the temporary pixel pin.");
        Console.WriteLine("Program icon checks passed: real executable resource pixels, missing/non-PE fallback and repeated GDI/icon handle cleanup. No application window or program was launched.");
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
