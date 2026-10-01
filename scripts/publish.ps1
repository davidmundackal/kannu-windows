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
$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

# The hook runs on every agent event: Native AOT so it starts in milliseconds with no runtime needed.
dotnet publish "$root/src/Kannu.Hook" -c Release -r $Runtime -o $Output @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The app needs the .NET 8 Desktop Runtime; the installer adds it when missing.
dotnet publish "$root/src/Kannu.App" -c Release -r $Runtime --self-contained false -o $Output @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $Output"
