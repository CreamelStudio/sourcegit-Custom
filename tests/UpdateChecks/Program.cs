using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using SourceGit;
using SourceGit.Models;
using ReleaseVersion = SourceGit.Models.Version;

static void Check(bool condition, string description)
{
    if (!condition)
        throw new Exception(description);
}

var current = new System.Version(2026, 22, 0, 0);
Check(ReleaseVersion.IsNewer("v2026.22.1", current), "A patch release must be detected.");
foreach (var tag in new[] { "v2026.22", "v2026.22.0.0", "v2026.21", "v2026.22.1-beta", "invalid", "", null })
    Check(!ReleaseVersion.IsNewer(tag, current), $"Not a newer stable version: {tag}");

var release = JsonSerializer.Deserialize("""
    {"tag_name":"v2026.22.1","published_at":"2026-10-22T02:30:00Z","assets":[
        {"name":"sourcegit_2026.22.1.win-x64.zip","size":123,"digest":"sha256:abcd"},
        {"name":"sourcegit_2026.22.1.win-arm64.zip","size":456,"digest":"sha256:efgh"}
    ]}
    """, JsonCodeGen.Default.Version)!;
Check(release.FindAsset("win-x64").Size == 123, "Select the x64 package.");
Check(release.FindAsset("win-arm64").Size == 456, "Select the ARM64 package.");
Check(release.FindAsset("linux-x64") == null, "Never select a different platform package.");
Check(release.PublishedAt.Year == 2026, "Read GitHub's release timestamp.");
using (var installer = typeof(SelfUpdater).Assembly.GetManifestResourceStream("SourceGit.Resources.Scripts.update-windows.ps1"))
    Check(installer != null && installer.Length > 0, "The Windows installer must be embedded in the application.");

var installed = SelfUpdater.GetInstalledRelease(null);
Check(installed != null && installed.IsInstalled, "The first launch after upgrading from an older app must show installed release notes.");
Check(installed.TagName == installed.CurrentVersionStr && !installed.IsNewVersion && !installed.CanAutoUpdate, "Installed notes must describe this binary and offer no installation action.");
Check(!string.IsNullOrWhiteSpace(installed.Body), "Installed release notes must work without a network request.");
Check(SelfUpdater.GetInstalledRelease(installed.TagName) == null, "Do not show the same installed version again.");
Check(SelfUpdater.GetInstalledRelease("v2026.22.2")?.TagName == installed.TagName, "Show notes after updating from the previous release.");

var preferences = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.Preferences)!;
Check(string.IsNullOrEmpty(preferences.LastShownReleaseTag), "Existing preference files must migrate without an installed-release marker.");
preferences.LastShownReleaseTag = installed.TagName;
var savedPreferences = JsonSerializer.Serialize(preferences, JsonCodeGen.Default.Preferences);
var restoredPreferences = JsonSerializer.Deserialize(savedPreferences, JsonCodeGen.Default.Preferences)!;
Check(SelfUpdater.GetInstalledRelease(restoredPreferences.LastShownReleaseTag) == null, "Persist the displayed version across restarts.");

var root = Path.Combine(Path.GetTempPath(), "SourceGit.UpdateChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string MakeZip(string name, params string[] entries)
    {
        var path = Path.Combine(root, name + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("package fixture");
        }
        return path;
    }

    var valid = MakeZip("valid", "SourceGit/SourceGit.exe", "SourceGit/library.dll");
    var payload = Path.Combine(root, "payload");
    SelfUpdater.ExtractPackage(valid, payload);
    Check(File.Exists(Path.Combine(payload, "SourceGit", "SourceGit.exe")), "Extract the expected application.");

    var index = 0;
    foreach (var entry in new[] { "../outside.exe", "SourceGit/../outside.exe", "SourceGit/data/settings.json", "SourceGit/data", "SourceGit/app.exe:stream", "Other/file.dll", "SourceGit\\..\\outside.exe", "SourceGit/SourceGit.EXE" })
    {
        var invalid = MakeZip("invalid" + index, "SourceGit/SourceGit.exe", entry);
        var rejected = false;
        try { SelfUpdater.ExtractPackage(invalid, Path.Combine(root, "invalid" + index)); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, $"Reject unsafe or duplicate path: {entry}");
        index++;
    }

    var asset = new ReleaseAsset
    {
        Size = new FileInfo(valid).Length,
        Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(valid))),
    };
    await SelfUpdater.VerifyPackageAsync(valid, asset, CancellationToken.None);
    asset.Digest = "sha256:" + new string('0', 64);
    var mismatchRejected = false;
    try { await SelfUpdater.VerifyPackageAsync(valid, asset, CancellationToken.None); }
    catch (InvalidDataException) { mismatchRejected = true; }
    Check(mismatchRejected, "Reject a corrupted package.");
    asset.Size++;
    var sizeRejected = false;
    try { await SelfUpdater.VerifyPackageAsync(valid, asset, CancellationToken.None); }
    catch (InvalidDataException) { sizeRejected = true; }
    Check(sizeRejected, "Reject a truncated download.");
}
finally
{
    Directory.Delete(root, true);
}

Console.WriteLine("Update checks passed: versions, platform assets, installed release notes and persisted acknowledgement, safe ZIP paths, package size and SHA-256.");
