# Releasing Kannu for Windows

A release is a tag. Pushing `vX.Y.Z` runs `.github/workflows/release.yml`, which tests, builds the
installer with [Velopack](https://velopack.io), signs it when the signing secrets exist, and publishes a
GitHub Release on this repository. Installed copies of Kannu read that release feed and update
themselves. This is the Windows counterpart of macOS Kannu's DMG + Sparkle pipeline.

## What a release contains

| File                       | What                                                                    |
|----------------------------|-------------------------------------------------------------------------|
| `Kannu-win-Setup.exe`      | The installer users download. Per-user, no admin prompt, Start-menu entry, includes .NET, so nothing else is installed. |
| `Kannu-win-Portable.zip`   | Kannu without an installer; runs from any folder.                       |
| `Kannu-X.Y.Z-full.nupkg`   | The full update package.                                                |
| `Kannu-X.Y.Z-delta.nupkg`  | The update from the previous release (only what changed); absent on the first release. |
| `releases.win.json`, `RELEASES`, `assets.win.json` | The update feed installed copies read.        |

Title: `Kannu for Windows X.Y.Z — <Codename>`. Body: `docs/release-notes/X.Y.Z.md`.

## Steps

1. On `development`, set `<Version>` in `Directory.Build.props` and write
   `docs/release-notes/X.Y.Z.md` (user-facing: what changed for them, not the commit list). The test
   suite fails if the notes file for the current version is missing.
2. For a feature release, cut the `## [Unreleased]` entries in `CHANGELOG.md` into a `## [X.Y.Z]`
   section. Decide the codename (`ReleaseInfo.Codename` in `src/Kannu.Core/ReleaseInfo.cs`): a feature
   release may change it, a patch release keeps it.
3. Branch `release/X.Y.Z` from `development`, open a PR into `main`, merge it when CI is green.
4. Tag the merge commit on `main` and push the tag:

   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

   Or, without a terminal: on GitHub, **Releases › Draft a new release**, type the tag
   (`vX.Y.Z`, or `vX.Y.Z-beta.N` with **Set as a pre-release** ticked), target the branch to release,
   and publish. That creates the tag, the tag runs the workflow, and the workflow adds the installer
   and update files to that release (`vpk upload --merge`).

5. Watch the **Release** workflow. The workflow refuses a tag whose version differs from
   `<Version>` in `Directory.Build.props`.
6. Merge `main` back into `development`.

## Test releases

Tag `vX.Y.Z-test.N` (the part before `-` must still equal `<Version>`). It publishes a GitHub
**pre-release**. Only a build installed from a test release follows test releases; stable installs never
see them. To check updating end to end: install from `-test.1`, push `-test.2`, wait for (or click)
**Check for Updates…**, restart, and confirm the tray shows the new version and agents' hooks still run.

## Signing

Releases are unsigned until a code-signing certificate is bought, and SmartScreen warns on first run
(the README tells users how to get past it). To sign, add these repository secrets; the workflow then
signs `Kannu.exe`, `kannu-hook.exe` and the installer with no other change:

| Secret                        | Value                                                    |
|-------------------------------|----------------------------------------------------------|
| `WINDOWS_SIGN_CERT_BASE64`    | The code-signing certificate (`.pfx`), base64-encoded    |
| `WINDOWS_SIGN_CERT_PASSWORD`  | Its password                                             |

`GITHUB_TOKEN` (provided by Actions) uploads the release; no other secret is needed.

## Building the installer locally

```powershell
dotnet tool install -g vpk --version <the Velopack version in src/Kannu.App/Kannu.App.csproj>
./scripts/build-installer.ps1
```

The output is in `Releases/`, unsigned. `vpk` and the `Velopack` package must stay on the same version;
the workflows read the package version from `Kannu.App.csproj`, so updating that one line is enough.

## How updating works

`Program.Main` hands control to Velopack first. On install and on every update, Velopack runs the new
version once with a lifecycle argument; Kannu then copies its `kannu-hook.exe` to
`%LOCALAPPDATA%\Kannu\bin` and rewrites the entries of every agent that already had Kannu's hook, so a
hook never points at an old build. Before uninstalling, it removes Kannu's entries from every agent.

Running Kannu checks the feed at launch and daily (`UpdateService`), downloads in the background and
applies the update the next time Kannu starts. Debug builds and builds run straight from `out/` never
update.
