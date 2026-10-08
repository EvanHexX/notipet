# Opens every notipet window and page for real and checks it survives.
#
# Compiling and --self-test cannot catch WinUI render-time failures: NumberBox
# killed the process with a stowed exception (0xc000027b) that no managed
# handler sees, and a TimePicker crushed a label to one glyph per line. Both
# were found only by opening the window. This script does that, and saves a
# screenshot of each page to look at.
#
#   .\scripts\ui-check.ps1                                   # uses bin\
#   .\scripts\ui-check.ps1 -FromBuildOutput -Configuration Debug
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$FromBuildOutput,
    [string]$ShotDir = (Join-Path $env:TEMP 'notipet-ui')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$daemon = if ($FromBuildOutput) {
    Join-Path $root "app\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe"
} else {
    Join-Path $root 'bin\NotipetTray.exe'
}
if (-not (Test-Path $daemon)) { throw "not found: $daemon" }
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'NotipetUi').Type) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class NotipetUi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@ }
# Per-monitor aware, or on a scaled display every coordinate we read is
# virtualised and screenshots land on the wrong pixels.
[NotipetUi]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

$script:failed = 0
function Report([bool]$ok, [string]$what) {
    Write-Host ("[{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $script:failed++ }
}
function Alive { (@(Get-Process NotipetTray -ErrorAction SilentlyContinue)).Count -gt 0 }
function Shot($element, [string]$name) {
    $r = $element.Current.BoundingRectangle
    if ($r.Width -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $ShotDir "$name.png"))
}

$crashMark = Get-Date
if (-not (Alive)) { Start-Process $daemon | Out-Null; Start-Sleep 4 }
Report (Alive) 'daemon running'

$uia = [System.Windows.Automation.AutomationElement]
# The long-running daemon (oldest), not a --self-test that may be running too.
$pid0 = [int](Get-Process NotipetTray | Sort-Object StartTime | Select-Object -First 1).Id
$byPid = New-Object System.Windows.Automation.PropertyCondition($uia::ProcessIdProperty, $pid0)
function Windows { $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $byPid) }
function Descendants($e) { $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }

# ----- settings: every page -----
Start-Process $daemon -ArgumentList '--settings' | Out-Null; Start-Sleep 3
$settings = Windows | Where-Object { $_.Current.Name -match 'Settings|설정' } | Select-Object -First 1
Report ($null -ne $settings) 'settings window opens'
if ($settings) {
    [NotipetUi]::SetForegroundWindow([IntPtr]$settings.Current.NativeWindowHandle) | Out-Null
    $listItem = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $pages = @($settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItem))
    Report ($pages.Count -ge 6) "settings has $($pages.Count) pages"
    $i = 0
    foreach ($page in $pages) {
        $name = $page.Current.Name
        try { $page.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
        Start-Sleep -Milliseconds 900
        $errors = @(Descendants $settings | Where-Object { $_.Current.Name -match '^\[\w+\]|Exception' }).Count
        Report ((Alive) -and $errors -eq 0) "settings page '$name' renders"
        Shot $settings ("settings-{0}" -f $i); $i++
    }
}

# ----- recent notifications -----
Start-Process $daemon | Out-Null; Start-Sleep 3
$recent = Windows | Where-Object { $_.Current.Name -match 'Recent|최근' } | Select-Object -First 1
Report (($null -ne $recent) -and (Alive)) 'recent window opens'
if ($recent) { Shot $recent 'recent' }

# ----- tray left click toggles the recent window -----
# From a known hidden state: click -> shown, click -> hidden, click -> shown.
# Synthetic clicks do not move the foreground to the taskbar the way a real
# click does, so this covers the z-order logic, not the real-click path (that
# one is on the manual checklist in docs/modules/tray_shell.md).
$tray = [NotipetUi]::FindWindow('NotipetTrayWindow', [NullString]::Value)
function TrayClick { [NotipetUi]::PostMessage($tray, 0x8001, [IntPtr]1, [IntPtr]0x0202) | Out-Null; Start-Sleep -Milliseconds 900 }
if ($recent) {
    # A ringing alarm would take the first click (stopping the alarm comes
    # first, by design) and shift the whole sequence. Silence it first.
    try {
        $rt = Get-Content (Join-Path $env:LOCALAPPDATA 'notipet\runtime.json') -Raw | ConvertFrom-Json
        Invoke-RestMethod -Method Post -Uri "$($rt.baseUrl)/v1/ack" -Headers @{ Authorization = "Bearer $($rt.token)" } `
            -ContentType 'application/json' -Body '{"all":true}' | Out-Null
    } catch { }
    $recentHwnd = [IntPtr]$recent.Current.NativeWindowHandle
    try { $recent.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    Start-Sleep -Milliseconds 600
    $hiddenAtStart = -not [NotipetUi]::IsWindowVisible($recentHwnd)
    TrayClick; $shown = [NotipetUi]::IsWindowVisible($recentHwnd)
    TrayClick; $hidden = -not [NotipetUi]::IsWindowVisible($recentHwnd)
    TrayClick; $shownAgain = [NotipetUi]::IsWindowVisible($recentHwnd)
    Report ($hiddenAtStart -and $shown -and $hidden -and $shownAgain -and (Alive)) 'tray left click toggles the recent window (show / hide / show)'
}

# ----- tray menu -----
[NotipetUi]::PostMessage($tray, 0x8001, [IntPtr]1, [IntPtr]0x0205) | Out-Null   # WM_APP+1 / WM_RBUTTONUP
Start-Sleep -Milliseconds 900
$menu = [NotipetUi]::FindWindow('#32768', $null)
Report (($menu -ne [IntPtr]::Zero) -and [NotipetUi]::IsWindowVisible($menu) -and (Alive)) 'tray menu opens (icons are drawn here)'
if ($menu -ne [IntPtr]::Zero) { [NotipetUi]::PostMessage($menu, 0x0100, [IntPtr]0x1B, [IntPtr]0) | Out-Null }

# ----- crash reports during the run -----
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $crashMark } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -eq 'Application Error' -and $_.Message -match 'NotipetTray' })
Report ($crashes.Count -eq 0) "no NotipetTray crash in the event log ($($crashes.Count))"

Write-Host ''
Write-Host "screenshots: $ShotDir" -ForegroundColor Cyan
if ($script:failed -eq 0) { Write-Host 'ui-check: all passed' -ForegroundColor Green }
else { Write-Host "ui-check: $script:failed failed" -ForegroundColor Red }
exit $script:failed
