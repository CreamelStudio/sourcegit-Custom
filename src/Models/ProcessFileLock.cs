using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal static class ProcessFileLock
    {
        public static FileStream TryOpen(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
        }

        public static async Task<FileStream> EnterAsync(string path, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var file = TryOpen(path);
                if (file != null)
                    return file;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }

        public static string RepositoryPath(string commonDir)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDir));
            if (OperatingSystem.IsWindows())
                normalized = normalized.ToUpperInvariant();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceGit", "operation-locks", key + ".lock");
        }
    }
}
