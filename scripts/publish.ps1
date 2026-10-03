# Builds Kannu.exe and kannu-hook.exe into one folder. The tray's "Agent hooks" menu copies
# kannu-hook.exe from next to Kannu.exe, so they must ship together.
#
#   ./scripts/publish.ps1                       # win-x64 into out/win-x64
#   ./scripts/publish.ps1 -Runtime win-arm64
#   ./scripts/publish.ps1 -Version 0.1.0-test.1 # what the release workflow does for a test tag

param(
    [string]$Runtime = 'win-x64',
    [string]$Output = "out/$Runtime",
    # Overrides <Version> in Directory.Build.props; empty keeps it.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Built up explicitly: `if` returns a one-element array as a bare string, and splatting a string
# passes it one character at a time (the first beta release failed on exactly that).
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

# The hook runs on every agent event: Native AOT so it starts in milliseconds with no runtime needed.
dotnet publish "$root/src/Kannu.Hook" -c Release -r $Runtime -o $Output @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Self-contained: .NET ships inside Kannu, so installing never stops to run Microsoft's separate
# .NET Desktop Runtime installer (which beta testers hit as an extra, confusing prompt).
dotnet publish "$root/src/Kannu.App" -c Release -r $Runtime --self-contained true -o $Output @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $Output"
