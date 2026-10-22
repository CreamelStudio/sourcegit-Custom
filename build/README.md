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

## How to build this project manually

1. Make sure [.NET SDK 10](https://dotnet.microsoft.com/en-us/download) is installed on your machine.
2. Clone this project
3. Run the follow command under the project root dir
```sh
dotnet publish -c Release -r $RUNTIME_IDENTIFIER -o $DESTINATION_FOLDER src/SourceGit.csproj
```
> [!NOTE]
> Please replace the `$RUNTIME_IDENTIFIER` with one of `win-x64`,`win-arm64`,`linux-x64`,`linux-arm64`,`osx-x64`,`osx-arm64`, and replace the `$DESTINATION_FOLDER` with the real path that will store the output executable files.
