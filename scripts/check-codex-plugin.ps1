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

# The SHA-256 of hooks\hooks.json (LF line endings). Codex trusts each hook by
# the hash of its definition, so ANY change to that file makes every user
# review and trust the hooks again - until they do, notipet stays silent for
# them. Change hooks.json only when it is worth that; then update this value and
# say so in the release notes. Logic that may change belongs in
# scripts\notipet-hook.cmd, which the hash does not cover.
$TrustedHooksSha256 = 'fb1e20cf791f688a15f96bb3bd38ab612207fdb5289f0485849a1863c474567b'   # 1.5.1, first plugin release

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

# --- manifest version = the app's version --------------------------------------
$manifestPath = Join-Path $plugin '.codex-plugin\plugin.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$appVersion = ([xml](Get-Content (Join-Path $root 'app\Notipet.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Check ($manifest.name -eq 'notipet') 'plugin.json name is "notipet"' 'The name is the plugin id (notipet@<marketplace>) and the trust key - do not rename it.'
Check ($manifest.version -eq $appVersion) "plugin.json version ($($manifest.version)) = app version ($appVersion)" 'Bump .codex-plugin\plugin.json "version" with the csproj <Version>. Codex caches plugins per version.'
Check ($manifest.skills -eq './skills/') 'plugin.json points skills at ./skills/'
Check (-not (Test-Path (Join-Path $plugin 'plugin.json'))) 'no root plugin.json' 'A root plugin.json makes Codex read the plugin as the portable format, which loads NO hooks.'

# --- marketplace ---------------------------------------------------------------
$market = Get-Content (Join-Path $root '.agents\plugins\marketplace.json') -Raw | ConvertFrom-Json
$entry = $market.plugins | Where-Object name -eq 'notipet'
Check ($null -ne $entry) 'marketplace.json lists notipet'
if ($entry) {
    $src = Join-Path $root ($entry.source.path -replace '^\./', '' -replace '/', '\')
    Check ((Resolve-Path $src -ErrorAction SilentlyContinue).Path -eq (Resolve-Path $plugin).Path) "marketplace path $($entry.source.path) is the plugin folder"
}

# --- hooks.json ----------------------------------------------------------------
$hooksPath = Join-Path $plugin 'hooks\hooks.json'
$hooksText = [IO.File]::ReadAllText($hooksPath)
$hooks = $hooksText | ConvertFrom-Json
$top = @($hooks.PSObject.Properties.Name)
Check (@($top | Where-Object { $_ -notin 'description', 'hooks' }).Count -eq 0) 'hooks.json has only "description" and "hooks" at the top' 'Codex rejects the whole file on an unknown top-level key.'
$events = @($hooks.hooks.PSObject.Properties.Name)
Check (($events | Sort-Object) -join ',' -eq 'PermissionRequest,Stop,UserPromptSubmit') "hooks.json events: $($events -join ', ')" 'Adding or removing an event changes what users trust; see docs\modules\codex_plugin.md.'
$handlers = foreach ($e in $events) { foreach ($g in $hooks.hooks.$e) { foreach ($h in $g.hooks) { $h } } }
Check (@($handlers | Where-Object { $_.PSObject.Properties.Name -contains 'args' }).Count -eq 0) 'no "args" field' 'Codex has no args field and ignores it silently; arguments go in the command line.'
Check (@($handlers | Where-Object { $_.commandWindows -ne '"${PLUGIN_ROOT}\scripts\notipet-hook.cmd" --source codex' }).Count -eq 0) 'every hook runs scripts\notipet-hook.cmd --source codex'
$normalized = $hooksText.Replace("`r`n", "`n")
$sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))).ToLowerInvariant()
Check ($sha -eq $TrustedHooksSha256) "hooks.json unchanged (sha256 $($sha.Substring(0, 12))...)" "hooks.json changed: every user must trust the hooks again. If that is intended, set `$TrustedHooksSha256 = '$sha' in this script and put it in the release notes."

# --- the hook script -----------------------------------------------------------
$script = Join-Path $plugin 'scripts\notipet-hook.cmd'
$scriptBytes = [IO.File]::ReadAllBytes($script)
Check ([Text.Encoding]::ASCII.GetString($scriptBytes).Contains("`r`n")) 'notipet-hook.cmd has CRLF line endings' 'cmd.exe misreads labels and blocks in LF batch files; .gitattributes pins *.cmd to CRLF.'
Check (@($scriptBytes | Where-Object { $_ -gt 127 }).Count -eq 0) 'notipet-hook.cmd is plain ASCII' 'cmd.exe reads batch files in the console code page.'
# Run it the way Codex does: cmd.exe /C with ${PLUGIN_ROOT} replaced in the
# text. `version` exercises finding the exe without sending anything.
$commandLine = $hooks.hooks.Stop[0].hooks[0].commandWindows.Replace('${PLUGIN_ROOT}', $plugin).Replace(' --source codex', ' version')
function RunHookCommand([hashtable]$envOverrides) {
    $psi = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
    $psi.Arguments = "/C `"$commandLine`""
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    foreach ($k in $envOverrides.Keys) { $psi.Environment[$k] = $envOverrides[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd(); $p.WaitForExit()
    [pscustomobject]@{ Code = $p.ExitCode; Out = $out.Trim() }
}
$r = RunHookCommand @{ NOTIPET_CLI = (Resolve-Path $Cli).Path }
Check ($r.Code -eq 0 -and $r.Out -match 'notipet') "the hook command runs through cmd.exe /C ($(($r.Out -split "`n")[0]))"
$r = RunHookCommand @{ NOTIPET_CLI = (Join-Path $env:TEMP 'no-such-notipet.exe'); LOCALAPPDATA = (Join-Path $env:TEMP 'no-such-localappdata'); PATH = "$env:SystemRoot\System32" }
Check ($r.Code -eq 0 -and $r.Out -eq '') 'without notipet anywhere it exits 0 and prints nothing'

# --- the skill = the Codex template rendered with plain `notipet` --------------
$skillDir = Join-Path $plugin 'skills'
if ($Fix) { & $Cli install-skill --codex --command notipet --path $skillDir --force | Out-Host }
$expected = (& $Cli install-skill --codex --command notipet --print | Out-String).TrimEnd()
$actual = if (Test-Path (Join-Path $skillDir 'notipet\SKILL.md')) { (Get-Content (Join-Path $skillDir 'notipet\SKILL.md') -Raw).TrimEnd() } else { '' }
Check ($expected.Replace("`r`n", "`n") -eq $actual.Replace("`r`n", "`n")) 'skills\notipet\SKILL.md matches integrations\codex\skills\notipet (rendered with `notipet`)' 'Run with -Fix after editing the Codex skill template (and build the CLI first).'

Write-Host ''
if ($fails) { Write-Host "$fails check(s) failed" -ForegroundColor Red } else { Write-Host 'codex plugin ok' -ForegroundColor Green }
exit $fails
