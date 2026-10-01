# Builds Kannu.exe and kannu-hook.exe into one folder. The tray's "Install Claude Code hooks" looks
# for kannu-hook.exe next to Kannu.exe, so they must ship together.
#
#   ./scripts/publish.ps1                       # win-x64 into out/win-x64
#   ./scripts/publish.ps1 -Runtime win-arm64

param(
    [string]$Runtime = 'win-x64',
    [string]$Output = "out/$Runtime"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# The hook runs on every agent event: Native AOT so it starts in milliseconds with no runtime needed.
dotnet publish "$root/src/Kannu.Hook" -c Release -r $Runtime -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The app needs the .NET 8 Desktop Runtime installed.
dotnet publish "$root/src/Kannu.App" -c Release -r $Runtime --self-contained false -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $Output"
