# Checks the Codex plugin (integrations\codex\plugin) against the rest of the
# repository, so a release cannot ship a plugin that drifted. pack.ps1 runs it.
# What each check protects is in docs\modules\codex_plugin.md.
#
#   .\scripts\check-codex-plugin.ps1                 # check only
#   .\scripts\check-codex-plugin.ps1 -Fix            # regenerate the plugin's SKILL.md first
#   .\scripts\check-codex-plugin.ps1 -Cli <exe>      # use that notipet.exe (default: a build output)
#
# Exit code: the number of failed checks.
[CmdletBinding()]
param(
    [string]$Cli,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$plugin = Join-Path $root 'integrations\codex\plugin'
$fails = 0
function Check([bool]$ok, [string]$what, [string]$hint = '') {
    if ($ok) { Write-Host "[ ok ] $what" -ForegroundColor Green }
    else { Write-Host "[FAIL] $what" -ForegroundColor Red; if ($hint) { Write-Host "       $hint" -ForegroundColor Yellow }; $script:fails++ }
}

# --- the CLI that renders the skill -------------------------------------------
if (-not $Cli) {
    $Cli = @(
        (Join-Path $root 'artifacts\publish\notipet.exe'),
        (Join-Path $root 'cli\bin\Debug\net10.0\win-x64\notipet.exe'),
        (Join-Path $root 'bin\notipet.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Cli -or -not (Test-Path $Cli)) { throw 'No notipet.exe to render the skill with. Build the CLI or pass -Cli.' }
Write-Host "cli: $Cli"

# --- manifest ------------------------------------------------------------------
$manifestPath = Join-Path $plugin '.codex-plugin\plugin.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$appVersion = ([xml](Get-Content (Join-Path $root 'app\Notipet.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Check ($manifest.name -eq 'notipet') 'plugin.json name is "notipet"' 'The name is the plugin id (notipet@<marketplace>) - renaming it orphans every install.'
Check ($manifest.version -eq $appVersion) "plugin.json version ($($manifest.version)) = app version ($appVersion)" 'Bump .codex-plugin\plugin.json "version" with the csproj <Version>. Codex caches plugins per version.'
Check ($manifest.skills -eq './skills/') 'plugin.json points skills at ./skills/'

# --- skills only: what the public plugin directory accepts ---------------------
# Plugins with lifecycle hooks or app references cannot be submitted, and hooks
# run only in the desktop app. Hooks go through `notipet install-hooks --codex
# --write` instead (docs\modules\codex_plugin.md).
$props = @($manifest.PSObject.Properties.Name)
Check (-not ($props -contains 'hooks') -and -not (Test-Path (Join-Path $plugin 'hooks'))) 'no hooks in the plugin' 'Hooks keep the plugin out of the public directory; they are set up by install-hooks --codex --write.'
Check (-not ($props -contains 'apps') -and -not (Test-Path (Join-Path $plugin '.app.json'))) 'no app references'
Check (-not ($props -contains 'mcpServers') -and -not (Test-Path (Join-Path $plugin '.mcp.json'))) 'no MCP servers' 'A skills-only plugin cannot gain an MCP server later; that would be a new plugin.'
$extra = @(Get-ChildItem $plugin -Force | Where-Object { $_.Name -notin '.codex-plugin', 'skills', 'assets' } | ForEach-Object Name)
Check ($extra.Count -eq 0) "nothing but .codex-plugin, skills, assets$(if ($extra) { " (found: $($extra -join ', '))" })"

# --- marketplace ---------------------------------------------------------------
$market = Get-Content (Join-Path $root '.agents\plugins\marketplace.json') -Raw | ConvertFrom-Json
$entry = $market.plugins | Where-Object name -eq 'notipet'
Check ($market.name -eq 'notipet') 'marketplace name is "notipet"' 'It is the other half of the plugin id notipet@notipet.'
Check ($null -ne $entry) 'marketplace.json lists notipet'
if ($entry) {
    $src = Join-Path $root ($entry.source.path -replace '^\./', '' -replace '/', '\')
    Check ((Resolve-Path $src -ErrorAction SilentlyContinue).Path -eq (Resolve-Path $plugin).Path) "marketplace path $($entry.source.path) is the plugin folder"
}

# --- the skill = the Codex template rendered with plain `notipet` --------------
$skillDir = Join-Path $plugin 'skills'
if ($Fix) { & $Cli install-skill --codex --command notipet --path $skillDir --force | Out-Host }
$expected = (& $Cli install-skill --codex --command notipet --print | Out-String).TrimEnd()
$actual = if (Test-Path (Join-Path $skillDir 'notipet\SKILL.md')) { (Get-Content (Join-Path $skillDir 'notipet\SKILL.md') -Raw).TrimEnd() } else { '' }
Check ($expected.Replace("`r`n", "`n") -eq $actual.Replace("`r`n", "`n")) 'skills\notipet\SKILL.md matches integrations\codex\skills\notipet (rendered with `notipet`)' 'Run with -Fix after editing the Codex skill template (and build the CLI first).'
Check ($actual.Contains('install-hooks --codex --write')) 'the skill tells Codex how to set up the hooks'

Write-Host ''
if ($fails) { Write-Host "$fails check(s) failed" -ForegroundColor Red } else { Write-Host 'codex plugin ok' -ForegroundColor Green }
exit $fails
