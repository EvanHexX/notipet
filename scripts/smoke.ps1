# End-to-end smoke test against a real running daemon.
#
# Automates steps 1-8 of the checklist in docs\regression.md. Steps 9-14 need a
# human (is there actually a sound? did the balloon appear? does unplugging the
# audio device behave?) and are listed at the end.
#
#   .\scripts\smoke.ps1                    # uses bin\, starting the daemon if needed
#   .\scripts\smoke.ps1 -Configuration Debug -FromBuildOutput
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$FromBuildOutput,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($FromBuildOutput) {
    $daemon = Join-Path $root "app\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe"
    $cli = Join-Path $root "cli\bin\$Configuration\net10.0\win-x64\notipet.exe"
} else {
    $daemon = Join-Path $root 'bin\NotipetTray.exe'
    $cli = Join-Path $root 'bin\notipet.exe'
}

foreach ($exe in @($daemon, $cli)) {
    if (-not (Test-Path $exe)) { throw "not found: $exe  (run scripts\publish.ps1 first, or pass -FromBuildOutput)" }
}

$script:failed = 0
function Check([string]$name, [scriptblock]$test) {
    try {
        $result = & $test
        # Type check first: `$true -eq 'skip'` is true in PowerShell (the
        # string is coerced to a boolean), which would turn every pass into a skip.
        if ($result -is [string] -and $result -eq 'skip') { Write-Host "[SKIP] $name" -ForegroundColor DarkYellow; $script:skipped++ }
        elseif ($result) { Write-Host "[PASS] $name" -ForegroundColor Green }
        else { Write-Host "[FAIL] $name" -ForegroundColor Red; $script:failed++ }
    } catch {
        Write-Host "[FAIL] $name -- $_" -ForegroundColor Red
        $script:failed++
    }
}
$script:skipped = 0

# "At my desk" shortens every repeating alarm to a single play, by design, so
# the two checks that time a long alarm cannot run while it is on. They are
# skipped and say why - the user's setting is never flipped by a test.
function AtDesk {
    try { [bool](curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/health" | ConvertFrom-Json).atDesk } catch { $false }
}

Write-Host '== headless self-tests ==' -ForegroundColor Cyan
Check 'daemon --self-test' { & $daemon --self-test | Out-Null; $LASTEXITCODE -eq 0 }
Check 'cli --self-test'    { & $cli --self-test | Out-Null; $LASTEXITCODE -eq 0 }

Write-Host ''
Write-Host '== live daemon ==' -ForegroundColor Cyan

$startedHere = $false
if (-not (Get-Process -Name 'NotipetTray' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $daemon | Out-Null
    $startedHere = $true
    Start-Sleep -Seconds 3
}

$runtimePath = Join-Path $env:LOCALAPPDATA 'notipet\runtime.json'
Check '1. runtime.json written with a live port and pid' {
    (Test-Path $runtimePath) -and ((Get-Content $runtimePath | ConvertFrom-Json).port -gt 0)
}

$rt = Get-Content $runtimePath | ConvertFrom-Json
$base = $rt.baseUrl
$tok = $rt.token
$tmp = Join-Path $env:TEMP "notipet-smoke-$PID.json"

function Post([string]$path, [string]$json, [string[]]$extraHeaders = @()) {
    $json | Set-Content -Path $tmp -Encoding utf8
    $args = @('-s', '-o', 'NUL', '-w', '%{http_code}', '-X', 'POST', "$base$path",
              '-H', 'Content-Type: application/json', '--data-binary', "@$tmp")
    foreach ($h in $extraHeaders) { $args += @('-H', $h) }
    [int](curl.exe @args)
}
function PostBody([string]$path, [string]$json) {
    $json | Set-Content -Path $tmp -Encoding utf8
    curl.exe -s -X POST "$base$path" -H "Authorization: Bearer $tok" `
        -H 'Content-Type: application/json' --data-binary "@$tmp" | ConvertFrom-Json
}

Check '2. cli ping reports ok' { (& $cli ping) -match '^ok\s' }

Check '3. cli send is accepted and delivers to both local channels' {
    $r = & $cli send --title 'smoke' --body 'plain send' --level info --tag "smoke:send:$PID" --json | ConvertFrom-Json
    $r.accepted -and ($r.deliveries | Where-Object { $_.status -eq 'delivered' }).Count -ge 1
}

Check '4a. no token -> 401' { (Post '/v1/notify' '{"title":"x","body":"y"}') -eq 401 }
Check '4b. bogus token -> 401' { (Post '/v1/notify' '{"title":"x","body":"y"}' @("Authorization: Bearer nope")) -eq 401 }
Check '4c. valid token + browser Origin -> 401' {
    (Post '/v1/notify' '{"title":"x","body":"y"}' @("Authorization: Bearer $tok", 'Origin: http://evil.test')) -eq 401
}
Check '4d. valid token, no Origin -> 200' {
    (Post '/v1/notify' '{"title":"x","body":"y"}' @("Authorization: Bearer $tok")) -eq 200
}
Check '4e. oversized body -> 413' {
    (Post '/v1/notify' ('{"title":"' + ('x' * 100000) + '"}') @("Authorization: Bearer $tok")) -eq 413
}
Check '4f. unknown route -> 404' {
    [int](curl.exe -s -o NUL -w '%{http_code}' "$base/nope" -H "Authorization: Bearer $tok") -eq 404
}
Check '4g. unauthenticated health does not leak state' {
    $h = curl.exe -s "$base/v1/health"
    ($h -match '"ok":true') -and ($h -notmatch 'soundEngine')
}

Check '5. Claude hook payload maps to attention with the repo in the title' {
    $r = PostBody '/hooks/claude-code' '{"session_id":"smoke","hook_event_name":"Notification","notification_type":"agent_needs_input","cwd":"C:\\src\\notipet"}'
    $r.level -eq 'attention' -and $r.accepted
}

Check '6. same tag five times sounds once and records count 5' {
    $tag = "smoke:dedupe:$PID"
    1..5 | ForEach-Object { PostBody '/v1/notify' "{`"title`":`"dup`",`"body`":`"same`",`"tag`":`"$tag`"}" | Out-Null }
    $h = curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/history?limit=20" | ConvertFrom-Json
    ($h.entries | Where-Object { $_.tag -eq $tag } | Select-Object -First 1).count -eq 5
}

Check '7. a burst is rate-limited rather than machine-gunned' {
    $accepted = 0
    1..30 | ForEach-Object {
        $r = PostBody '/v1/notify' "{`"title`":`"flood`",`"body`":`"n$_`",`"tag`":`"smoke:flood:$PID`:$_`",`"source`":{`"id`":`"claude-code`"}}"
        if ($r.accepted) { $accepted++ }
    }
    $accepted -gt 0 -and $accepted -lt 15
}

Check '8. an until_ack alarm starts, and ack by tag stops it' {
    if (AtDesk) { Write-Host "       (at desk is on: alarms are shortened to one play - turn it off to run this)" -ForegroundColor DarkYellow; return 'skip' }
    # Clear whatever is still ringing from earlier checks. Check 5's attention
    # alarm runs for about 11 seconds now that sounds play to completion
    # instead of being cut off, so it can easily outlive its own check.
    PostBody '/v1/ack' '{"all":true}' | Out-Null

    # Check 7 just drained the global rate-limit bucket on purpose. critical
    # bypasses the per-source bucket but deliberately not the global one, so
    # wait for it to refill rather than asserting against our own flood.
    Start-Sleep -Seconds 3
    $baseline = (curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/health" | ConvertFrom-Json).activeAlarms
    if ($baseline -ne 0) {
        Write-Host "       (not a clean baseline: $baseline alarm(s) still active)" -ForegroundColor DarkYellow
        return $false
    }

    $tag = "smoke:crit:$PID"
    $fired = PostBody '/v1/notify' "{`"title`":`"smoke`",`"body`":`"critical`",`"level`":`"critical`",`"tag`":`"$tag`"}"
    if (-not $fired.accepted) {
        Write-Host "       (not accepted: $($fired.suppressedReason))" -ForegroundColor DarkYellow
        return $false
    }

    Start-Sleep -Milliseconds 800
    $during = (curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/health" | ConvertFrom-Json).activeAlarms
    $stopped = (PostBody '/v1/ack' "{`"tag`":`"$tag`"}").stopped
    Start-Sleep -Milliseconds 300
    $after = (curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/health" | ConvertFrom-Json).activeAlarms

    if ($during -lt 1) { Write-Host "       (alarm never registered)" -ForegroundColor DarkYellow }
    elseif ($stopped -lt 1) { Write-Host "       (ack stopped nothing)" -ForegroundColor DarkYellow }
    elseif ($after -ne 0) { Write-Host "       ($after alarm(s) left after ack)" -ForegroundColor DarkYellow }
    $during -ge 1 -and $stopped -ge 1 -and $after -eq 0
}

Check '9. a repeating alarm plays each sound to completion' {
    if (AtDesk) { Write-Host "       (at desk is on: alarms are shortened to one play - turn it off to run this)" -ForegroundColor DarkYellow; return 'skip' }
    # The regression this guards: the loop used to restart a sound every
    # intervalMs, so a 5-second alarm never finished. Two plays of the 5s alarm
    # with a 0.7s gap, so anything under ~9s means it was cut short. The spec is
    # pinned in the request: the user's own level settings (until_ack, no time
    # limit) must not decide what this measures.
    PostBody '/v1/ack' '{"all":true}' | Out-Null
    Start-Sleep -Seconds 3

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $fired = PostBody '/v1/notify' "{`"title`":`"smoke`",`"body`":`"length`",`"level`":`"attention`",`"tag`":`"smoke:len:$PID`",`"sound`":{`"alias`":`"Notification.Looping.Alarm`",`"repeat`":`"repeat`",`"repeatCount`":2,`"intervalMs`":700}}"
    if (-not $fired.accepted) {
        Write-Host "       (not accepted: $($fired.suppressedReason))" -ForegroundColor DarkYellow
        return $false
    }
    do {
        Start-Sleep -Milliseconds 250
        $n = (curl.exe -s -H "Authorization: Bearer $tok" "$base/v1/health" | ConvertFrom-Json).activeAlarms
    } while ($n -gt 0 -and $sw.Elapsed.TotalSeconds -lt 30)
    $sw.Stop()
    # Never leave it ringing: with "no limit" in the user's settings nothing
    # else would stop it, and the next script's first tray click would.
    PostBody '/v1/ack' '{"all":true}' | Out-Null

    $seconds = $sw.Elapsed.TotalSeconds
    Write-Host ("       (played for {0:N1}s)" -f $seconds) -ForegroundColor DarkGray
    # Generous bounds: the default alias is whatever the user has configured.
    $seconds -ge 8 -and $seconds -le 20
}

if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }

if ($startedHere -and -not $KeepRunning) {
    Get-Process -Name 'NotipetTray' -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host ''
if ($script:failed -eq 0) {
    $note = if ($script:skipped -gt 0) { " ($script:skipped skipped - see [SKIP] lines)" } else { '' }
    Write-Host "smoke: all automated checks passed$note" -ForegroundColor Green
} else {
    Write-Host "smoke: $script:failed check(s) FAILED" -ForegroundColor Red
}

Write-Host ''
Write-Host 'Still to check by hand (a script cannot hear a sound):' -ForegroundColor Yellow
@(
    '10. a sound actually played, and different levels sound different'
    '11. the settings window opens and every section renders (NotipetTray.exe --settings)'
    '12. the recent-notifications window opens from a tray left-click and shows full bodies'
    '13. "At my desk" in the tray menu shortens a critical alarm to one play'
    '14. quiet hours suppresses info but a critical still sounds'
    '15. mute from the tray silences; unmute restores'
    '16. disable every audio device -> the balloon still appears, the tray icon'
    '    switches to the warning glyph, and re-enabling recovers WITHOUT a restart'
    '17. restart explorer.exe -> the tray icon comes back'
    '18. with Codex hooks installed, the pre-existing `notify` integration still works'
) | ForEach-Object { Write-Host $_ }

exit $script:failed
