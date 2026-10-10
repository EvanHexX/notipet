# Checks the Claude Code plugin (integrations\claude\plugin) against the rest of
# the repository, so a release cannot ship a plugin that drifted. pack.ps1 runs
# it. What each check protects is in docs\modules\claude_plugin.md.
#
#   .\scripts\check-claude-plugin.ps1                 # check only
#   .\scripts\check-claude-plugin.ps1 -Fix            # regenerate hooks.json and SKILL.md first
#   .\scripts\check-claude-plugin.ps1 -Cli <exe>      # use that notipet.exe (default: a build output)
#
# Exit code: the number of failed checks.
[CmdletBinding()]
param(
    [string]$Cli,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$plugin = Join-Path $root 'integrations\claude\plugin'
$fails = 0
function Check([bool]$ok, [string]$what, [string]$hint = '') {
    if ($ok) { Write-Host "[ ok ] $what" -ForegroundColor Green }
    else { Write-Host "[FAIL] $what" -ForegroundColor Red; if ($hint) { Write-Host "       $hint" -ForegroundColor Yellow }; $script:fails++ }
}
function Same([string]$a, [string]$b) { $a.Replace("`r`n", "`n").TrimEnd() -eq $b.Replace("`r`n", "`n").TrimEnd() }

if (-not $Cli) {
    $Cli = @(
        (Join-Path $root 'artifacts\publish\notipet.exe'),
        (Join-Path $root 'cli\bin\Debug\net10.0\win-x64\notipet.exe'),
        (Join-Path $root 'bin\notipet.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Cli -or -not (Test-Path $Cli)) { throw 'No notipet.exe to render the plugin files with. Build the CLI or pass -Cli.' }
Write-Host "cli: $Cli"

# --- manifest ------------------------------------------------------------------
$manifest = Get-Content (Join-Path $plugin '.claude-plugin\plugin.json') -Raw | ConvertFrom-Json
$appVersion = ([xml](Get-Content (Join-Path $root 'app\Notipet.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Check ($manifest.name -eq 'notipet') 'plugin.json name is "notipet"' 'The name is the plugin id (notipet@notipet) and every component''s namespace - renaming it orphans every install.'
Check ($manifest.version -eq $appVersion) "plugin.json version ($($manifest.version)) = app version ($appVersion)" 'Claude Code keeps users on a version until it changes: bump it with the csproj <Version>.'
Check (-not (Test-Path (Join-Path $plugin 'bin'))) 'no bin\ folder' 'claude.ai and Cowork refuse a plugin with a top-level bin\.'

# --- marketplace ---------------------------------------------------------------
$market = Get-Content (Join-Path $root '.claude-plugin\marketplace.json') -Raw | ConvertFrom-Json
$entry = $market.plugins | Where-Object name -eq 'notipet'
Check ($market.name -eq 'notipet' -and $market.owner.name) 'marketplace.json: name "notipet" and an owner'
Check ($null -ne $entry) 'marketplace.json lists notipet'
if ($entry) {
    $src = Join-Path $root ($entry.source -replace '^\./', '' -replace '/', '\')
    Check ((Resolve-Path $src -ErrorAction SilentlyContinue).Path -eq (Resolve-Path $plugin).Path) "marketplace source $($entry.source) is the plugin folder"
    Check (-not ($entry.PSObject.Properties.Name -contains 'version')) 'the entry leaves the version to plugin.json'
}

# --- generated files: hooks.json and the skill, from the CLI -----------------------
$hooksPath = Join-Path $plugin 'hooks\hooks.json'
$skillDir = Join-Path $plugin 'skills'
if ($Fix) {
    New-Item -ItemType Directory -Force (Split-Path $hooksPath) | Out-Null
    $generated = (& $Cli install-hooks --plugin | Out-String)
    [IO.File]::WriteAllText($hooksPath, $generated.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
    & $Cli install-skill --command notipet --path $skillDir --force | Out-Host
}
$expectedHooks = (& $Cli install-hooks --plugin | Out-String)
Check ((Test-Path $hooksPath) -and (Same $expectedHooks (Get-Content $hooksPath -Raw))) 'hooks\hooks.json = `notipet install-hooks --plugin`' 'Run with -Fix after changing cli\ClaudeHooks.cs.'
$hooks = Get-Content $hooksPath -Raw | ConvertFrom-Json
$handlers = foreach ($e in $hooks.hooks.PSObject.Properties) { foreach ($g in $e.Value) { foreach ($h in $g.hooks) { $h } } }
Check (@($handlers | Where-Object { $_.command -ne 'notipet.exe' -or -not $_.args -or -not $_.async }).Count -eq 0) 'every hook runs notipet.exe in exec form (args), async' 'Exec form needs no shell (Git Bash or not), and async keeps the turn from waiting.'

$expectedSkill = (& $Cli install-skill --command notipet --print | Out-String)
$skillPath = Join-Path $skillDir 'notipet\SKILL.md'
Check ((Test-Path $skillPath) -and (Same $expectedSkill (Get-Content $skillPath -Raw))) 'skills\notipet\SKILL.md = the Claude skill rendered with `notipet`' 'Run with -Fix after editing integrations\claude\skills\notipet\SKILL.md.'

# --- Claude Code's own validator, when it is installed -----------------------------
if (Get-Command claude -ErrorAction SilentlyContinue) {
    $out = (& claude plugin validate $plugin 2>&1 | Out-String)
    Check ($LASTEXITCODE -eq 0 -and $out -match 'Validation passed') "claude plugin validate (plugin): $(($out.Trim() -split "`n")[-1])"
    $out = (& claude plugin validate $root 2>&1 | Out-String)
    Check ($LASTEXITCODE -eq 0 -and $out -match 'Validation passed') "claude plugin validate (marketplace): $(($out.Trim() -split "`n")[-1])"
} else {
    Write-Host '[skip] claude plugin validate (claude is not on PATH)' -ForegroundColor DarkGray
}

Write-Host ''
if ($fails) { Write-Host "$fails check(s) failed" -ForegroundColor Red } else { Write-Host 'claude plugin ok' -ForegroundColor Green }
exit $fails
