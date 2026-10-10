# Builds the installer and the update feed with Velopack.
#
# Publishes both exes to artifacts\publish (not bin\ - a running dev daemon is
# left alone), then `vpk pack` writes to artifacts\releases:
#
#   NotipetApp-win-Setup.exe          the installer to hand out
#   NotipetApp-<version>-full.nupkg   the update package
#   releases.win.json, assets.win.json, RELEASES   the feed the app reads
#   NotipetApp-win-Portable.zip       an unzip-and-run copy
#
# The app installs to %LOCALAPPDATA%\NotipetApp\current (settings and logs stay
# in %LOCALAPPDATA%\notipet), with a Start menu shortcut and no desktop one.
# Publishing a release is a separate, deliberate step (docs/modules/installer.md).
#
#   .\scripts\pack.ps1                       # version from the csproj
#   .\scripts\pack.ps1 -Delta                # also a delta from the latest GitHub release
#   .\scripts\pack.ps1 -Version 1.5.0-rc.2 -Out artifacts\feed   # a local test feed
#
# Needs vpk: dotnet tool install -g vpk --version <same as the Velopack package>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Out = 'artifacts\releases',
    [switch]$Delta,
    [switch]$NoAot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$outDir = [IO.Path]::GetFullPath((Join-Path $root $Out))

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    $tools = Join-Path $env:USERPROFILE '.dotnet\tools'
    if (Test-Path (Join-Path $tools 'vpk.exe')) { $env:PATH = "$tools;$env:PATH" }
    else { throw 'vpk not found. Install it: dotnet tool install -g vpk' }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'app\Notipet.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "==> Notipet $Version" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$publishArgs = @{ OutDir = 'artifacts\publish'; Version = $Version }
if ($NoAot) { $publishArgs.NoAot = $true }
& (Join-Path $PSScriptRoot 'publish.ps1') @publishArgs
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

# The Codex plugin ships from the repository, not in this package, but it is
# versioned with it: a release must not leave it stale (docs\modules\codex_plugin.md).
Write-Host '==> Codex plugin check' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'check-codex-plugin.ps1') -Cli (Join-Path $publish 'notipet.exe')
if ($LASTEXITCODE -ne 0) { throw "Codex plugin check failed ($LASTEXITCODE) - fix it, or run check-codex-plugin.ps1 -Fix" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# A delta needs the previous full package next to the new one.
if ($Delta) {
    Write-Host '==> previous release (for a delta)' -ForegroundColor Cyan
    vpk download github --repoUrl https://github.com/EvanHexX/notipet --outputDir $outDir
    if ($LASTEXITCODE -ne 0) { throw "vpk download failed ($LASTEXITCODE)" }
}

Write-Host '==> vpk pack' -ForegroundColor Cyan
vpk pack `
    --packId NotipetApp `
    --packVersion $Version `
    --packDir $publish `
    --mainExe NotipetTray.exe `
    --packTitle Notipet `
    --packAuthors EvanHexX `
    --icon (Join-Path $root 'app\Assets\Notipet.ico') `
    --shortcuts StartMenuRoot `
    --outputDir $outDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host ''
Write-Host "Packed to $outDir" -ForegroundColor Green
Get-ChildItem $outDir -File | Where-Object { $_.Name -match [regex]::Escape($Version) -or $_.Name -notmatch '\d+\.\d+\.\d+' } |
    ForEach-Object { '{0,-44} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
