using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SourceGit.Native
{
    [SupportedOSPlatform("windows")]
    internal static class WindowsProgramIcon
    {
        internal const int Size = 32;
        private static readonly Dictionary<string, (long Stamp, Bitmap Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new();

        public static Bitmap Read(string executable)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                    return null;
                var stamp = File.GetLastWriteTimeUtc(executable).Ticks;
                lock (CacheLock)
                    if (Cache.TryGetValue(executable, out var cached) && cached.Stamp == stamp)
                        return cached.Icon;
                var pixels = ReadPixels(executable);
                if (pixels == null)
                    return null;
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                Bitmap icon;
                try { icon = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pin.AddrOfPinnedObject(), new PixelSize(Size, Size), new Vector(96, 96), Size * 4); }
                finally { pin.Free(); }
                lock (CacheLock)
                {
                    if (Cache.Count >= 64)
                        Cache.Clear();
                    Cache[executable] = (stamp, icon);
                }
                return icon;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        internal static byte[] ReadPixels(string executable)
        {
            var large = IntPtr.Zero;
            var small = IntPtr.Zero;
            var dc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;
            try
            {
                var count = ExtractIconEx(executable, 0, out large, out small, 1);
                if (count == 0 || count == uint.MaxValue)
                    return null;
                var icon = large != IntPtr.Zero ? large : small;
                if (icon == IntPtr.Zero)
                    return null;
                dc = CreateCompatibleDC(IntPtr.Zero);
                var header = new BitmapHeader { HeaderSize = 40, Width = Size, Height = -Size, Planes = 1, Bits = 32, ImageSize = Size * Size * 4 };
                bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
                if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                    return null;
                previous = SelectObject(dc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                    return null;

                var black = new byte[Size * Size * 4];
                Marshal.Copy(black, 0, bits, black.Length);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, IntPtr.Zero, 3))
                    return null;
                GdiFlush();
                Marshal.Copy(bits, black, 0, black.Length);
                var white = new byte[black.Length];
                Array.Fill(white, (byte)255);
                Marshal.Copy(white, 0, bits, white.Length);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, IntPtr.Zero, 3))
                    return null;
                GdiFlush();
                Marshal.Copy(bits, white, 0, white.Length);
                // Recover transparency for both alpha icons and older AND/XOR-mask icons.
                for (var i = 0; i < black.Length; i += 4)
                {
                    var difference = Math.Max(white[i] - black[i], Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2]));
                    black[i + 3] = (byte)(255 - Math.Clamp(difference, 0, 255));
                }
                return black;
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero && small != large) DestroyIcon(small);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint HeaderSize;
            public int Width, Height;
            public ushort Planes, Bits;
            public uint Compression, ImageSize;
            public int XPixelsPerMeter, YPixelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }

        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GdiFlush();
    }
}
