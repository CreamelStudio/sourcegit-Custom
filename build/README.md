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

## Missing worktrees

Branch/worktree refresh and checkout, branch and worktree-add operations clean stale worktree registrations using Git's metadata-only prune. The batch is eligible only when every unlocked prunable entry is a missing folder on a ready fixed local drive with an attached branch. Existing folders, detached HEADs, inaccessible paths and removable/network drives defer automatic pruning; locked entries remain protected by Git. Branches and working files are never deleted by this cleanup. Parent and submodule repositories have separate registrations and are checked independently when opened. A manually moved worktree should be reconnected with `git worktree repair` before refreshing its original repository.

## How to build this project manually

1. Make sure [.NET SDK 10](https://dotnet.microsoft.com/en-us/download) is installed on your machine.
2. Clone this project
3. Run the follow command under the project root dir
```sh
dotnet publish -c Release -r $RUNTIME_IDENTIFIER -o $DESTINATION_FOLDER src/SourceGit.csproj
```
> [!NOTE]
> Please replace the `$RUNTIME_IDENTIFIER` with one of `win-x64`,`win-arm64`,`linux-x64`,`linux-arm64`,`osx-x64`,`osx-arm64`, and replace the `$DESTINATION_FOLDER` with the real path that will store the output executable files.

## Project opener and compact commit filter

The small project button opens the current Unity project with its exact installed editor version, or the program chosen for that repository. Its adjacent arrow selects a discovered IDE, a solution/workspace, or another executable, and can change the target folder/file. Targets stay inside the repository and are stored relatively, so linked worktrees open their own project. Missing programs/targets return to selection. Unity projects nested under `Client` or submodules do not turn a server repository's button into Unity. On Windows the button and menu read the selected executable's icon without launching it; cached icons load off the UI thread. The launcher uses no bundled product logos; the button falls back to a generic symbol when extraction is unavailable, including on other operating systems. Shared images still used by the existing merge-tool selector are preserved. Explorer, terminal and one-time IDE options remain under Other open options.

In the history view, Ctrl+F (Cmd+F on macOS) opens a single-line filter above the existing commit graph. Typing filters the rows and graph by message/body, full or abbreviated hash, or author/email; the field selector also preserves path/glob and changed-content regex searches, with an optional current-branch scope. Queries are debounced and cancelled when superseded or closed, and show up to 1,000 matching commits across local refs and HEAD. Esc or Close restores the unfiltered history, including a repository refresh that arrived while filtering. The old large search panel and Ctrl+Shift+F route are removed. Older settings containing its obsolete width still load.

Application icons identify their respective launch targets; they do not imply vendor sponsorship. About includes ownership notices and vendor links. Reading an installed icon avoids shipping another logo file but does not itself grant trademark rights. JetBrains permits product-integration identification subject to its [brand guidelines](https://www.jetbrains.com/company/brand/), including attribution and a link. Unity's [trademark guidelines](https://unity.com/legal/branding-trademarks) have narrower corporate/cube-logo permissions and do not clearly approve arbitrary third-party launcher branding. No new official logo assets or "Made with Unity" badge are bundled; public logo-use clearance has not been obtained.

## Recovery, workspace search and background tasks

The small tools button beside the window controls opens Recovery timeline, Workspace search and Background tasks. Recovery pins commits before checkout/rebase/reset/merge/cherry-pick/revert/pull in local `refs/sourcegit/recovery/` and includes HEAD reflog entries. Creating a recovery branch does not checkout or reset files. These are committed-history recovery points, not backups of uncommitted/untracked files; saved refs are retained while the view shows the 500 most recent points.

Search uses repositories in the active preset (or workspace) plus initialized submodules, up to 64 repositories. Commit search matches messages across local refs; file search includes tracked and non-ignored untracked names; content search uses tracked non-binary working-tree files. Queries are literal and case-insensitive. Two repositories are searched at once, with cancellation, a 30-second command timeout, a 2 MiB output cap, 200 hits per repository and 1,000 displayed hits overall. Search runs only on request and reports limits/errors.

Clones, SSH-key-path setup and recursive submodule preparation run in an independent process before Avalonia initializes. A per-user durable queue and task states are stored under the config directory's `background` folder. The worker runs an isolated copy of the installed program, so UI updates can replace the original files. Worker version changes drain current jobs before handover; obsolete flat runtime copies are cleaned after handover. Clones use existing noninteractive Git credentials, reject credentials in HTTP URLs/clone arguments, and preserve failed/cancelled folders. This does not promise byte-level clone resume after logout, machine shutdown or a worker crash.

`Continue automatic Fetch after closing the app` defaults on, while preserving the existing global automatic-Fetch opt-out and interval. Opened repositories are registered and remain scheduled after their tabs close; remove individual paths in Background tasks or disable background Fetch in Git preferences. Remote opt-outs are read from current repository config on each fetch. Initial fetch waits one minute, failures retry after one minute, and successful/manual fetch times are shared. A busy UI lease/index lock pauses new automatic work. Four automatic fetches and two clones may run; per-common-directory file leases serialize app-owned remote operations across processes. This is not a Windows system service and does not install a login-startup entry. With no queued/running work or enabled Fetch schedule, the worker exits.
