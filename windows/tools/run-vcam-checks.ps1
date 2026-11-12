# RaaiCam virtual camera: automated end-to-end checks A-F (Phase 1A).
# Usage: powershell -ExecutionPolicy Bypass -File windows\tools\run-vcam-checks.ps1
# Needs the camera installed (windows\install\dev-install.ps1) and everything built (windows\build.ps1).
# NOTE: this script stops any running raaicam-dev and vcam-probe processes before and after the checks.
# Check F restarts the FrameServer service and therefore only runs when this shell is elevated (otherwise SKIP).
$ErrorActionPreference = 'Stop'
$scriptStart = Get-Date
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeExe = Join-Path $root 'windows\RaaiCam.VirtualCam\build\bin\vcam-probe.exe'
$devExe = Join-Path $root 'windows\tools\raaicam-dev\bin\Release\net10.0-windows\raaicam-dev.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $root "windows\out\checks\$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
foreach ($p in $probeExe, $devExe) { if (-not (Test-Path $p)) { throw "missing $p (run windows\build.ps1 first)" } }

$results = [ordered]@{}
$script:dev = $null
$script:bg = @()

function Now-Ms { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Add-Result($id, $status, $detail) {
  $results[$id] = [pscustomobject]@{ Status = $status; Detail = $detail }
  Write-Host ("[{0}] {1}  {2}" -f $id, $status, $detail)
}
function Fps-Ok($j) { $j.fps -ge 28 -and $j.fps -le 32 }
function Fmt($j) {
  "{0}x{1} fps={2} (ts {3}) frames={4} card={5} live={6} torn={7} other={8} distinctIds={9} maxDupRun={10} maxIdJump={11} backStep={12} err={13}" -f `
    $j.width, $j.height, $j.fps, $j.fpsByTimestamp, $j.frames, $j.cardFrames, $j.liveFrames, $j.tornFrames, $j.otherFrames, $j.distinctFrameIds, $j.maxDupRun, $j.maxIdJump, $j.maxIdBackStep, $j.error
}

function Stop-Dev {
  Get-Process -Name 'raaicam-dev' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $script:dev = $null
  Start-Sleep -Milliseconds 300
}
function Start-Dev($tag) {
  Stop-Dev
  $log = Join-Path $outDir "dev-$tag.log"
  $script:dev = Start-Process -FilePath $devExe -RedirectStandardOutput $log -RedirectStandardError (Join-Path $outDir "dev-$tag.err") -WindowStyle Hidden -PassThru
  $script:bg += $script:dev
  $script:devLog = $log
  Start-Sleep -Milliseconds 800
}

# Starts the probe without waiting. Returns @{Proc; Json; Log}.
function Start-Probe($tag, $w, $h, $frames) {
  $json = Join-Path $outDir "probe-$tag.json"
  $log = Join-Path $outDir "probe-$tag.log"
  $p = Start-Process -FilePath $probeExe -ArgumentList @('--name', 'RaaiCam', '--width', $w, '--height', $h, '--frames', $frames, '--json', $json) `
    -RedirectStandardOutput $log -WindowStyle Hidden -PassThru
  $script:bg += $p
  @{ Proc = $p; Json = $json; Log = $log }
}
function Read-ProbeJson($pr) {
  if (Test-Path $pr.Json) { Get-Content $pr.Json -Raw | ConvertFrom-Json } else { $null }
}
function Run-Probe($tag, $w, $h, $frames) {
  $pr = Start-Probe $tag $w $h $frames
  if (-not $pr.Proc.WaitForExit(120000)) { $pr.Proc.Kill() }
  $j = Read-ProbeJson $pr
  if (-not $j) { $j = [pscustomobject]@{ opened = $false; fps = 0; fpsByTimestamp = 0; frames = 0; width = 0; height = 0; cardFrames = 0; liveFrames = 0; tornFrames = 0; otherFrames = 0; distinctFrameIds = 0; error = 'no json produced' } }
  $j
}
function Get-Transitions($log) {
  $t = @()
  if (Test-Path $log) {
    foreach ($m in [regex]::Matches((Get-Content $log -Raw), 'TRANSITION frame=(\d+) wall_ms=(\d+) -> (\w+)')) {
      $t += [pscustomobject]@{ Frame = [int]$m.Groups[1].Value; Ms = [int64]$m.Groups[2].Value; To = $m.Groups[3].Value }
    }
  }
  $t
}
function Wait-LogMatch($path, $pattern, $timeoutMs) {
  $end = (Now-Ms) + $timeoutMs
  while ((Now-Ms) -lt $end) {
    if ((Test-Path $path) -and ((Get-Content $path -Raw -ErrorAction SilentlyContinue) -match $pattern)) { return $true }
    Start-Sleep -Milliseconds 50
  }
  $false
}
function Frame-ServerPid {
  (Get-CimInstance Win32_Service -Filter "Name='FrameServer'").ProcessId
}

try {
  Stop-Dev
  Get-Process -Name 'vcam-probe' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Write-Host "outputs: $outDir"

  # ---- A: no app running -> placeholder card at 30 fps
  $a = Run-Probe 'A' 1280 720 90
  $ok = $a.opened -and (Fps-Ok $a) -and $a.cardFrames -eq 90
  Add-Result 'A' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Fmt $a)

  # ---- B: raaicam-dev running, 1080p
  Start-Dev 'BC'
  $b = Run-Probe 'B' 1920 1080 300
  $ok = $b.opened -and $b.liveFrames -ge 290 -and $b.tornFrames -eq 0 -and $b.distinctFrameIds -ge 240 -and $b.maxDupRun -le 2 -and $b.maxIdJump -le 3 -and $b.maxIdBackStep -eq 0 -and (Fps-Ok $b)
  Add-Result 'B' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Fmt $b)

  # ---- C: same at 720p (resolution switch = consumer reopening at a new size)
  $c = Run-Probe 'C' 1280 720 300
  $ok = $c.opened -and $c.liveFrames -ge 290 -and $c.tornFrames -eq 0 -and $c.distinctFrameIds -ge 240 -and $c.maxDupRun -le 2 -and $c.maxIdJump -le 3 -and $c.maxIdBackStep -eq 0 -and (Fps-Ok $c)
  Add-Result 'C' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Fmt $c)

  # ---- D: kill raaicam-dev mid-capture -> card within 700 ms, fps stays 28-32
  Start-Dev 'D'
  $pr = Start-Probe 'D' 1280 720 450
  $live = Wait-LogMatch $pr.Log 'TRANSITION frame=\d+ wall_ms=\d+ -> LIVE' 15000
  if ($live) {
    Start-Sleep -Seconds 3
    $killMs = Now-Ms
    Stop-Process -Id $script:dev.Id -Force -ErrorAction SilentlyContinue
  }
  if (-not $pr.Proc.WaitForExit(120000)) { $pr.Proc.Kill() }
  $d = Read-ProbeJson $pr
  if (-not $live -or -not $d) {
    Add-Result 'D' 'FAIL' ("never saw live frames before the kill; json=" + $(if ($d) { Fmt $d } else { 'none' }))
  } else {
    $tr = Get-Transitions $pr.Log
    $firstCard = $tr | Where-Object { $_.To -eq 'CARD' -and $_.Ms -ge $killMs } | Select-Object -First 1
    $lat = if ($firstCard) { $firstCard.Ms - $killMs } else { $null }
    # After the first card the dead writer's frames must never come back (no card/live flicker).
    $relapse = 0
    if ($firstCard) { $relapse = @($tr | Where-Object { $_.Ms -gt $firstCard.Ms -and $_.To -ne 'CARD' }).Count }
    $ok = $d.opened -and $null -ne $lat -and $lat -le 700 -and (Fps-Ok $d) -and $relapse -eq 0
    $latStr = if ($null -ne $lat) { "$lat ms" } else { 'no card transition' }
    $first = ($tr | Select-Object -First 6 | ForEach-Object { "$($_.To)@$($_.Frame)/$($_.Ms)" }) -join ' '
    Add-Result 'D' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("kill->first card = $latStr; transitions away from card after it = $relapse; " + (Fmt $d) + "; kill at $killMs; transitions($($tr.Count)): $first")
  }
  Stop-Dev

  # ---- E: 10x start the probe and kill it mid-stream; Frame Server must survive and still serve the card
  $eFails = @()
  $null = Run-Probe 'E-warmup' 1280 720 30   # make sure Frame Server is up and hosting the camera
  $pidBefore = Frame-ServerPid
  if (-not $pidBefore) { $eFails += 'FrameServer host PID is 0 before the run (service not running)' }
  Start-Dev 'E'
  for ($i = 1; $i -le 10; $i++) {
    $pe = Start-Probe "E$i" 1280 720 3000
    Start-Sleep -Milliseconds (1500 + (Get-Random -Maximum 1500))
    if (-not $pe.Proc.HasExited) { Stop-Process -Id $pe.Proc.Id -Force -ErrorAction SilentlyContinue }
    $pe.Proc.WaitForExit(5000) | Out-Null
  }
  Stop-Dev
  Start-Sleep -Seconds 1
  $e2 = Run-Probe 'E-after' 1280 720 90
  $okAfter = $e2.opened -and (Fps-Ok $e2) -and $e2.cardFrames -eq 90
  if (-not $okAfter) { $eFails += 'post-run probe failed A: ' + (Fmt $e2) }
  $pidAfter = Frame-ServerPid
  if ($pidBefore -ne $pidAfter) { $eFails += "FrameServer host PID changed $pidBefore -> $pidAfter" }
  $evts = @()
  try {
    # "No events found" is a normal, non-terminating condition; any other query error fails the check.
    $evts = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $scriptStart } -ErrorAction Stop |
      Where-Object { $_.Message -match 'FrameServer' })
  } catch {
    if ($_.FullyQualifiedErrorId -notmatch 'NoMatchingEventsFound') { $eFails += "event log query failed: $($_.Exception.Message)" }
  }
  if ($evts.Count -gt 0) { $eFails += "$($evts.Count) Application Error 1000 event(s) for svchost.exe_FrameServer since the script started" }
  Add-Result 'E' $(if ($eFails.Count -eq 0) { 'PASS' } else { 'FAIL' }) ("FrameServer PID $pidBefore -> $pidAfter; app-error events=$($evts.Count); after-run: " + (Fmt $e2) + $(if ($eFails) { ' | ' + ($eFails -join '; ') } else { '' }))

  # ---- F: restart FrameServer while raaicam-dev runs (elevation required)
  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $isAdmin) {
    Add-Result 'F' 'SKIP' '(needs elevation)'
    Write-Host 'SKIP (needs elevation)'
  } else {
    Start-Dev 'F'
    $pf = Start-Probe 'F1' 1280 720 150
    $null = Wait-LogMatch $pf.Log 'TRANSITION frame=\d+ wall_ms=\d+ -> LIVE' 15000
    $genBefore = ([regex]::Matches((Get-Content $script:devLog -Raw), 'gen=(\d+)') | Select-Object -Last 1).Groups[1].Value
    $t0 = Now-Ms
    Restart-Service FrameServer -Force
    # A consumer is needed for the writer to see a new generation: start F2 right away, then poll the
    # raaicam-dev log for a new gen with the 5 s deadline measured from the restart (independent of F1 exiting).
    $pf2 = Start-Probe 'F2' 1280 720 120
    $newGen = $null
    while (((Now-Ms) - $t0) -lt 5000 -and -not $newGen) {
      $g = [regex]::Matches((Get-Content $script:devLog -Raw -ErrorAction SilentlyContinue), 'gen=(\d+)') | Select-Object -Last 1
      if ($g -and $g.Groups[1].Value -ne $genBefore) { $newGen = $g.Groups[1].Value }
      Start-Sleep -Milliseconds 100
    }
    $genMs = Now-Ms - $t0
    if (-not $pf.Proc.WaitForExit(20000)) { $pf.Proc.Kill() }
    if (-not $pf2.Proc.WaitForExit(60000)) { $pf2.Proc.Kill() }
    $f2 = Read-ProbeJson $pf2
    $ok = $newGen -and $f2 -and $f2.liveFrames -gt 0
    Add-Result 'F' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("gen $genBefore -> $newGen after $genMs ms; probe after restart: " + $(if ($f2) { Fmt $f2 } else { 'no json' }))
    Stop-Dev
  }
}
finally {
  Stop-Dev
  foreach ($p in $script:bg) { try { if ($p -and -not $p.HasExited) { $p.Kill() } } catch {} }
}

Write-Host ''
Write-Host '=== summary ==='
foreach ($k in $results.Keys) { Write-Host ("{0}: {1}" -f $k, $results[$k].Status) }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir 'summary.json') -Encoding utf8
if (($results.Values | Where-Object { $_.Status -eq 'FAIL' })) { exit 1 }
exit 0

