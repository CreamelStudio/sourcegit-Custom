using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using SourceGit;
using SourceGit.Models;
using ReleaseVersion = SourceGit.Models.Version;

if (args.Length == 5 && args[0] == "--background-submit")
{
    await WorkspaceChecks.SubmitAsync(args[1], args[2], args[3], args[4]);
    return;
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new Exception(description);
}

var current = new System.Version(2026, 22, 0, 0);
PresetChecks.Run();
UnityChecks.Run();
ProjectOpenerChecks.Run();
ProgramIconChecks.Run();
Check(ReleaseVersion.IsNewer("v2026.22.1", current), "A patch release must be detected.");
Check(ReleaseVersion.IsNewer("v1.0.0", current), "The new version scheme must supersede the old date-based versions.");
var v1 = new System.Version(1, 0, 0, 0);
Check(!ReleaseVersion.IsNewer("v1.0.0", v1), "Never offer the installed version again.");
Check(ReleaseVersion.IsNewer("v1.0.1", v1), "A bug fix increments the patch version.");
Check(ReleaseVersion.IsNewer("v1.1.0", new System.Version(1, 0, 9, 0)), "A feature increments the minor version.");
Check(ReleaseVersion.IsNewer("v2.0.0", new System.Version(1, 99, 99, 0)), "A major redesign increments the major version.");
Check(!ReleaseVersion.IsNewer("v2026.22.5", v1), "The migration alias must not cause an update loop after installing v1.");
foreach (var tag in new[] { "v2026.22", "v2026.22.0.0", "v2026.21", "v2026.22.1-beta", "invalid", "", null })
    Check(!ReleaseVersion.IsNewer(tag, current), $"Not a newer stable version: {tag}");
foreach (var tag in new[] { "v1.1", "v1.1.0.0", "v1.1.0-beta", "v1.1.0+build", "1.1.0" })
    Check(!ReleaseVersion.IsNewer(tag, v1), $"Require a stable vMAJOR.MINOR.PATCH tag: {tag}");

var releases = JsonSerializer.Deserialize("""
    [
      {"tag_name":"v2026.22.5"},
      {"tag_name":"v1.1.1"},
      {"tag_name":"v3.0.0","draft":true},
      {"tag_name":"v4.0.0","prerelease":true},
      {"tag_name":"v5.0.0-beta"},
      {"tag_name":"v2.0.0"},
      {"tag_name":"v1.9.0"}
    ]
    """, JsonCodeGen.Default.ListVersion)!;
Check(SelfUpdater.SelectLatestRelease(releases)?.TagName == "v2.0.0", "Select the highest stable semantic version, ignoring legacy aliases, drafts and prereleases.");
Check(SelfUpdater.SelectLatestRelease([new() { TagName = "v2026.22.5" }]) == null, "Do not fall back to the migration alias when no semantic releases are available.");

if (OperatingSystem.IsWindows())
{
    Check(SourceGit.Native.Windows.RefreshExecutableIcon(Environment.ProcessPath), "Windows must accept an icon refresh for an existing executable.");
    Check(!SourceGit.Native.Windows.RefreshExecutableIcon(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")), "Do not notify the Shell for missing executables.");
}

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
try
{
    await FetchChecks.RunAsync();
    await WorktreeChecks.RunAsync();
    await CommitFilterChecks.RunAsync();
    await WorkspaceChecks.RunAsync();
PresetChecks.CheckLauncher();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
