using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public static class SelfUpdater
    {
        public const string RepositoryUrl = "https://github.com/CreamelStudio/sourcegit-Custom";

        public static Version GetInstalledRelease(string lastShownTag)
        {
            var version = new Version { IsInstalled = true };
            version.TagName = version.CurrentVersionStr;
            if (lastShownTag == version.TagName)
                return null;

            using var stream = typeof(SelfUpdater).Assembly.GetManifestResourceStream("SourceGit.Resources.ReleaseNotes.md")!;
            using var reader = new StreamReader(stream);
            version.Body = reader.ReadToEnd();
            return version;
        }

        public static async Task<Version> CheckAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SourceGit-Custom");
            // The legacy /latest release stays pinned so 2026.x clients can migrate to 1.0.0.
            using var response = await client.GetAsync("https://api.github.com/repos/CreamelStudio/sourcegit-Custom/releases?per_page=100");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            var releases = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), JsonCodeGen.Default.ListVersion);
            return SelectLatestRelease(releases);
        }

        internal static Version SelectLatestRelease(IEnumerable<Version> releases)
        {
            Version latest = null;
            System.Version latestVersion = null;
            if (releases == null)
                return null;

            // shortcut: Check 100 recent releases; paginate if backports can hide the newest stable release.
            foreach (var release in releases)
            {
                if (release.IsDraft || release.IsPrerelease)
                    continue;
                var version = Version.ParseTag(release.TagName);
                if (version == null || version.Major == 2026)
                    continue;
                if (latestVersion == null || version > latestVersion)
                {
                    latest = release;
                    latestVersion = version;
                }
            }

            return latest;
        }

        public static async Task InstallAsync(Version version, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!version.CanAutoUpdate)
                throw new InvalidOperationException("Automatic installation requires a Windows ZIP release.");

            var executable = Environment.ProcessPath;
            if (!string.Equals(Path.GetFileName(executable), "SourceGit.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run the packaged SourceGit.exe to install an update.");

            var target = Path.GetDirectoryName(executable)!;
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The application directory must not be a symbolic link.");

            var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var asset = version.FindAsset(runtime);
            if (asset.Size <= 0 || asset.Size > 256 * 1024 * 1024 || asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("The release is missing a valid package size or SHA-256 checksum.");

            var updateDir = Path.Combine(Path.GetTempPath(), "SourceGit", "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(updateDir);
            var handedOff = false;
            try
            {
                var archivePath = Path.Combine(updateDir, "update.zip");
                var url = $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(version.TagName)}/{Uri.EscapeDataString(asset.Name)}";
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException("The update download requires HTTPS.");

                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = File.Create(archivePath);
                    var buffer = new byte[64 * 1024];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        received += count;
                        if (received > asset.Size)
                            throw new InvalidDataException("The update package is larger than its published size.");

                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        progress?.Report(received * 100.0 / asset.Size);
                    }
                }

                await VerifyPackageAsync(archivePath, asset, cancellationToken);
                var payload = Path.Combine(updateDir, "payload");
                await Task.Run(() => ExtractPackage(archivePath, payload), cancellationToken);
                foreach (var file in Directory.GetFiles(Path.Combine(payload, "SourceGit")))
                {
                    var destination = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("An application file is a symbolic link.");
                }

                // Check write access before asking the running application to exit.
                var probe = Path.Combine(target, $".sourcegit-update-{Guid.NewGuid():N}");
                using (File.Create(probe))
                { }
                File.Delete(probe);

                var scriptPath = Path.Combine(updateDir, "update.ps1");
                using (var script = typeof(SelfUpdater).Assembly.GetManifestResourceStream("SourceGit.Resources.Scripts.update-windows.ps1")!)
                using (var output = File.Create(scriptPath))
                    await script.CopyToAsync(output, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                    start.ArgumentList.Add(argument);
                start.Environment["SOURCEGIT_UPDATE_TARGET"] = target;
                start.Environment["SOURCEGIT_UPDATE_PID"] = Environment.ProcessId.ToString();
                using var updater = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
                handedOff = true;
            }
            finally
            {
                if (!handedOff)
                    Directory.Delete(updateDir, true);
            }
        }

        internal static async Task VerifyPackageAsync(string path, ReleaseAsset asset, CancellationToken cancellationToken)
        {
            if (new FileInfo(path).Length != asset.Size || asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("The update package size or checksum is missing or invalid.");

            await using var stream = File.OpenRead(path);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken);
            var expected = Convert.FromHexString(asset.Digest.Substring(7));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new InvalidDataException("The update package SHA-256 checksum does not match.");
        }

        internal static void ExtractPackage(string archivePath, string destination)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long size = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName == "SourceGit/")
                    continue;

                var parts = entry.FullName.Split('/');
                // shortcut: Windows releases contain only top-level app files; extend this when the package layout changes.
                if (parts.Length != 2 || parts[0] != "SourceGit" || string.IsNullOrEmpty(parts[1]) ||
                    parts[1] is "." or ".." || parts[1].Equals("data", StringComparison.OrdinalIgnoreCase) ||
                    parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || parts[1].EndsWith('.') || parts[1].EndsWith(' ') ||
                    !names.Add(parts[1]) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("The update ZIP contains an unsupported or unsafe path.");

                size += entry.Length;
                if (size > 512 * 1024 * 1024 || names.Count > 64)
                    throw new InvalidDataException("The update ZIP exceeds the application package limits.");
            }

            if (!names.Contains("SourceGit.exe"))
                throw new InvalidDataException("SourceGit.exe was not found in the update ZIP.");

            ZipFile.ExtractToDirectory(archivePath, destination);
        }
    }
}
