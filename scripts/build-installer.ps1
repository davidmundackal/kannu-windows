# Builds the installer locally, unsigned: the Windows counterpart of macOS Kannu's build-dmg.sh.
# The release workflow runs the same steps on a tag (and signs when its secrets exist).
#
#   ./scripts/build-installer.ps1              # version from Directory.Build.props
#   ./scripts/build-installer.ps1 -Version 0.1.0-test.1
#
# Needs vpk, the Velopack CLI, at the same version as the Velopack package in Kannu.App.csproj:
#   dotnet tool install -g vpk --version <that version>
#
# Output, in Releases/: Kannu-win-Setup.exe (the installer), Kannu-win-Portable.zip, the .nupkg and
# releases.win.json (the update feed).

param(
    [string]$Version = '',
    [string]$Runtime = 'win-x64',
    [string]$Output = 'Releases',
    # Reuse out/<Runtime> from an earlier publish.ps1 run (CI does).
    [switch]$SkipPublish,
    # signtool arguments, e.g. '/f cert.pfx /p <password> /fd sha256 /tr <timestamp url> /td sha256'.
    # Empty: unsigned, and Windows SmartScreen warns on first run.
    [string]$SignParams = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$publish = "$root/out/$Runtime"
if (-not $SkipPublish) {
    & "$PSScriptRoot/publish.ps1" -Runtime $Runtime -Output $publish -Version $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$notes = "$root/docs/release-notes/$($Version -replace '-.*$', '').md"
# Arrays built explicitly, never from an `if` expression: see publish.ps1.
$notesArgs = @()
if (Test-Path $notes) { $notesArgs += '--releaseNotes', $notes }
$signArgs = @()
if ($SignParams) { $signArgs += '--signParams', $SignParams }

vpk pack `
    --packId Kannu `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Kannu.exe `
    --packTitle Kannu `
    --packAuthors 'Kannu Contributors' `
    --icon "$root/assets/Kannu.ico" `
    --framework net8.0-x64-desktop `
    --shortcuts StartMenuRoot `
    --outputDir "$root/$Output" `
    @notesArgs `
    @signArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installer: $root/$Output/Kannu-win-Setup.exe"
