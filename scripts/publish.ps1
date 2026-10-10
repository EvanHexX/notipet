# Publishes both executables into bin\ at the repo root, which is the path the
# integration examples point at.
#
#   .\scripts\publish.ps1
#   .\scripts\publish.ps1 -Restart    # stop the running daemon, publish, start the new one
#   .\scripts\publish.ps1 -NoAot      # if the NativeAOT toolchain is unavailable
#   .\scripts\publish.ps1 -OutDir artifacts\publish   # elsewhere (pack.ps1 does this)
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoAot,
    [switch]$Restart,
    [string]$OutDir,
    # Stamps both exes with this version instead of the csproj's (pack.ps1).
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = if ($OutDir) { [IO.Path]::GetFullPath((Join-Path $root $OutDir)) } else { Join-Path $root 'bin' }
$cliExe = Join-Path $out 'notipet.exe'

# The daemon holds a lock on its own exe. With -Restart, ask it to quit
# cleanly (so runtime.json is removed) and wait for it; otherwise say so plainly
# rather than failing halfway with MSB3027. Only a daemon running from the
# output folder is in the way - an installed one elsewhere is not.
$wasRunning = [bool](Get-Process -Name 'NotipetTray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($out + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($wasRunning) {
    if (-not $Restart) {
        throw 'NotipetTray.exe is running and would lock the output. Re-run with -Restart, or quit it from the tray.'
    }
    if (Test-Path $cliExe) { & $cliExe stop | Out-Host }
    Start-Sleep -Milliseconds 300
    # A daemon too old to know /v1/shutdown, or one that did not answer.
    Get-Process -Name 'NotipetTray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($out + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

# The NativeAOT targets shell out to a bare `vswhere.exe` to locate the MSVC
# linker. Visual Studio installs it at a fixed path but does not put it on PATH,
# so outside a Developer Command Prompt the AOT link fails with MSB3073. Put it
# on PATH for this process rather than making the caller remember.
if (-not $NoAot -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if (Test-Path (Join-Path $installer 'vswhere.exe')) {
        $env:PATH = "$installer;$env:PATH"
    } else {
        Write-Warning 'vswhere.exe not found; NativeAOT will likely fail. Re-run with -NoAot for the ReadyToRun fallback.'
    }
}

Write-Host '==> daemon (WinUI 3, unpackaged, self-contained)' -ForegroundColor Cyan
[string[]]$versionArg = if ($Version) { @("-p:Version=$Version") } else { @() }

dotnet publish (Join-Path $root 'app\Notipet.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -o $out @versionArg
if ($LASTEXITCODE -ne 0) { throw "daemon publish failed ($LASTEXITCODE)" }

# NativeAOT is what keeps CLI startup at single-digit milliseconds, which
# matters because every hook invocation pays it. ReadyToRun is the fallback:
# still fast enough, and it does not need the MSVC toolchain.
#
# [string[]] is load-bearing: PowerShell unwraps a single-element array to a
# bare string, and splatting a string passes it one character at a time.
[string[]]$cliArgs = @(
    'publish', (Join-Path $root 'cli\Notipet.Cli.csproj')
    '-c', $Configuration, '-r', 'win-x64', '-o', $out
) + $versionArg
$cliArgs += if ($NoAot) {
    [string[]]@('-p:PublishAot=false', '-p:PublishReadyToRun=true', '-p:PublishSingleFile=true', '--self-contained', 'true')
} else {
    [string[]]@('-p:PublishAot=true')
}

Write-Host '==> cli' -ForegroundColor Cyan
& dotnet @cliArgs
if ($LASTEXITCODE -ne 0) { throw "cli publish failed ($LASTEXITCODE)" }

Write-Host ''
Write-Host "Published to $out" -ForegroundColor Green
Get-ChildItem $out -Filter '*.exe' | ForEach-Object {
    '{0,-16} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB)
}
Write-Host ''
Write-Host 'Verify:' -ForegroundColor Cyan
Write-Host "  & '$out\NotipetTray.exe' --self-test"
Write-Host "  & '$out\notipet.exe' --self-test"

if ($Restart) {
    Write-Host ''
    Write-Host '==> restart' -ForegroundColor Cyan
    & $cliExe start | Out-Host
}
