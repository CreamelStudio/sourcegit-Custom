# build

> [!WARNING]
> The files under the `build` folder is used for `Github Action` only, **NOT** for end users.

## Automatic builds with GitHub Actions

- Pushing to any branch runs **Continuous Integration**, which builds and packages Windows, macOS, and Linux for x64 and ARM64. Download the `package.*` artifacts from the workflow run.
- Pushing a tag starting with `v` (for example, `v1.0.0`) runs **Release**, which builds the same packages and attaches them to a GitHub Release. Reserved legacy `v2026.*` migration tags do not start builds.
- Both workflows use the existing build and packaging workflows. No additional repository secrets are required; releases use the automatic `GITHUB_TOKEN`.
- To build a branch manually, open **Actions → Continuous Integration → Run workflow**.

Before releasing, update `VERSION` to match the tag without the `v` prefix and commit the change. Then push the commit and the tag:

```sh
git push origin master
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin v1.0.0
```

A local tag starts a build only after it is pushed to GitHub. Branch pushes and tag pushes run their respective workflows separately.

The Release workflow requires the tag version to match `VERSION`. Packages are uploaded to a draft release before it is published, so update checks see complete releases. A manual Release run must select a `v*` tag.

## In-app updates

Versions use `MAJOR.MINOR.PATCH`: bug fixes increment PATCH (`1.0.1`), features increment MINOR and reset PATCH (`1.1.0`), and major changes or redesigns increment MAJOR and reset the rest (`2.0.0`). The first release under this scheme is **Release v1.0.0**.

The custom app checks `CreamelStudio/sourcegit-Custom` releases at startup and every hour when startup update checks are enabled. It uses the public GitHub API without a user token. It selects the highest stable `vMAJOR.MINOR.PATCH` from the 100 most recent releases and excludes drafts, prereleases and the legacy 2026 version scheme.

The GitHub `/releases/latest` endpoint stays pinned to the `v2026.22.5` migration release for older clients that cannot compare 2026.x with 1.x. This release contains the exact v1.0.0 Windows ZIPs with legacy filenames and explains the transition. The v1.0.0 workflow publishes it after all canonical packages are available. Future semantic releases are published with `--latest=false`; **do not change the pinned migration release**. Updated clients select semantic releases from the releases list, so they do not reinstall the migration alias or get stuck on v1.0.0. No credentials or existing settings are migrated through release metadata.

Windows ZIP releases support download, SHA-256 verification against the GitHub release asset digest, application file replacement, rollback on replacement/restart failure, and restart. The installer preserves portable `data` and unrelated files. macOS/Linux retain release-download installation.

The updater first becomes available after installing a release containing it. Future releases must keep the `sourcegit_<version>.win-x64.zip` / `win-arm64.zip` naming and flat `SourceGit/` application-file layout.

Update `src/Resources/ReleaseNotes.md` with each release. It is embedded in the app and shown once after installing a different version, before online update checks. The acknowledged version is saved in preferences after the window closes, so the installed notes work offline and are not replaced by a newer release's notes.

Updater checks run in the Windows x64 build. To run them locally with .NET 10 and Windows PowerShell:

```sh
dotnet run --project tests/UpdateChecks -c Release -p:DisableAOT=true
powershell -NoProfile -File tests/UpdateChecks/windows.ps1
```

## How to build this project manually

1. Make sure [.NET SDK 10](https://dotnet.microsoft.com/en-us/download) is installed on your machine.
2. Clone this project
3. Run the follow command under the project root dir
```sh
dotnet publish -c Release -r $RUNTIME_IDENTIFIER -o $DESTINATION_FOLDER src/SourceGit.csproj
```
> [!NOTE]
> Please replace the `$RUNTIME_IDENTIFIER` with one of `win-x64`,`win-arm64`,`linux-x64`,`linux-arm64`,`osx-x64`,`osx-arm64`, and replace the `$DESTINATION_FOLDER` with the real path that will store the output executable files.
