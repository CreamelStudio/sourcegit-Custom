# build

> [!WARNING]
> The files under the `build` folder is used for `Github Action` only, **NOT** for end users.

## Automatic builds with GitHub Actions

- Pushing to any branch runs **Continuous Integration**, which builds and packages Windows, macOS, and Linux for x64 and ARM64. Download the `package.*` artifacts from the workflow run.
- Pushing a tag starting with `v` (for example, `v2026.22.1`) runs **Release**, which builds the same packages and attaches them to a GitHub Release.
- Both workflows use the existing build and packaging workflows. No additional repository secrets are required; releases use the automatic `GITHUB_TOKEN`.
- To build a branch manually, open **Actions → Continuous Integration → Run workflow**.

Before releasing, update `VERSION` to match the tag without the `v` prefix and commit the change. Then push the commit and the tag:

```sh
git push origin master
git tag -a v2026.22.1 -m "Release 2026.22.1"
git push origin v2026.22.1
```

A local tag starts a build only after it is pushed to GitHub. Branch pushes and tag pushes run their respective workflows separately.

The Release workflow requires the tag version to match `VERSION`. Packages are uploaded to a draft release before it is published, so update checks see complete releases. A manual Release run must select a `v*` tag.

## In-app updates

The custom app checks `CreamelStudio/sourcegit-Custom` releases at startup and every hour when startup update checks are enabled. It uses the public GitHub API without a user token. Only newer numeric `v*` versions are offered; GitHub's latest-release endpoint excludes drafts and prereleases.

Windows ZIP releases support download, SHA-256 verification against the GitHub release asset digest, application file replacement, rollback on replacement/restart failure, and restart. The installer preserves portable `data` and unrelated files. macOS/Linux retain release-download installation.

The updater first becomes available after installing a release containing it. Future releases must keep the `sourcegit_<version>.win-x64.zip` / `win-arm64.zip` naming and flat `SourceGit/` application-file layout.

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
