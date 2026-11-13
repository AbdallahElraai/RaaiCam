# RaaiCam phone end-to-end checks P1-P5 (Phase 1B): real iPhone video over USB into the virtual camera.
# Usage: powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1 [-Soak] [-SoakMinutes 10]
# Needs: everything built (windows\build.ps1), the camera installed (windows\install\dev-install.ps1), the iPhone
# plugged in by cable, unlocked, with the RaaiCam app open on the "Ready" screen.
# NOTE: this script stops any running raaicam-host, raaicam-dev and vcam-probe processes before and after the checks.
# P5 (soak, default 10 minutes) only runs with -Soak, so the default run takes about 3 minutes.
# Exit codes: 0 all PASS, 1 a check failed, 2 preflight failed (nothing was run).
param([switch]$Soak, [int]$SoakMinutes = 10)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeExe = Join-Path $root 'windows\RaaiCam.VirtualCam\build\bin\vcam-probe.exe'
$ctlExe = Join-Path $root 'windows\RaaiCam.VirtualCam\build\bin\vcamctl.exe'
$hostExe = Join-Path $root 'windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $root "windows\out\phone-checks\$stamp"
$logDir = Join-Path $env:LOCALAPPDATA 'RaaiCam\logs'

function Say($m) { Write-Host $m }
function Fail-Preflight($what, $howToFix) {
  Write-Host ''
  Write-Host "PREFLIGHT FAILED: $what" -ForegroundColor Red
  Write-Host "  What to do: $howToFix"
  Write-Host '  No checks were run.'
  exit 2
}

# ---------------------------------------------------------------- preflight
foreach ($p in $probeExe, $ctlExe, $hostExe) {
  if (-not (Test-Path $p)) { Fail-Preflight "missing $p" 'Build first: powershell -ExecutionPolicy Bypass -File windows\build.ps1' }
}
Get-Process -Name 'raaicam-host', 'raaicam-dev', 'vcam-probe' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$status = $null
try { $status = (& $ctlExe status) | Select-Object -Last 1 | ConvertFrom-Json } catch {}
if (-not $status -or -not $status.cameraPresent) {
  Fail-Preflight 'the RaaiCam virtual camera is not installed' 'Run (as Administrator) powershell -ExecutionPolicy Bypass -File windows\install\dev-install.ps1'
}
$dev = ""
try { $dev = (& $hostExe --list-devices | Out-String) } catch { $dev = "" }
$devCount = if ($dev -match 'devices=(\d+)') { [int]$Matches[1] } else { 0 }
$appState = if ($dev -match 'app=(\S+)') { $Matches[1] } else { 'n/a' }
if ($devCount -lt 1) {
  Fail-Preflight 'no iPhone is visible over USB' 'Plug the iPhone in with its cable, unlock it, tap "Trust This Computer" if asked, and make sure Apple Devices (or iTunes) is installed. Then run this script again.'
}
if ($appState -ne 'reachable') {
  Fail-Preflight 'the iPhone is connected but the RaaiCam app does not answer on port 47820' 'Unlock the iPhone and open the RaaiCam app (install it first with iloader if needed, see docs\checklists\phase-1b.md step 0). The screen should say "Ready". Then run this script again.'
}

New-Item -ItemType Directory -Force $outDir | Out-Null
Say "preflight OK (camera present, $devCount iPhone, app reachable); outputs: $outDir"

# ---------------------------------------------------------------- helpers
$results = [ordered]@{}
$script:hostProc = $null
$script:bg = @()
$hostOut = Join-Path $outDir 'host-stdout.txt'
$hostErr = Join-Path $outDir 'host-stderr.txt'
$logFile = Join-Path $logDir ("phone-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
$logStartLines = if (Test-Path $logFile) { @(Get-Content $logFile -ErrorAction SilentlyContinue).Count } else { 0 }

function Add-Result($id, $status, $detail) {
  $results[$id] = [pscustomobject]@{ Status = $status; Detail = $detail }
  Write-Host ("[{0}] {1}  {2}" -f $id, $status, $detail)
}
function Fps-Ok($j) { $j.fps -ge 28 -and $j.fps -le 32 }
function Read-Shared($path) {
  if (-not (Test-Path $path)) { return @() }
  try {
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { $sr = New-Object IO.StreamReader($fs); $t = $sr.ReadToEnd() } finally { $fs.Dispose() }
    return @($t -split "`r?`n" | Where-Object { $_ -ne '' })
  } catch { return @() }
}
function Host-Lines { Read-Shared $hostOut }
# Parses "link=usb phone=iPhone consumer=1920x1080 in=30.0 out=30.0 lat=48/71ms rtt=3ms 8.0Mbps thermal=nominal batt=82%+"
function Parse-Status($line) {
  if ($line -notmatch '^link=(\S+) phone=(.*?) consumer=(\S+) in=([\d.]+) out=([\d.]+) lat=(\S+) rtt=(\S+) (\S+) thermal=(\S+) batt=(\S+)') { return $null }
  $p50 = $null; $p95 = $null
  if ($Matches[6] -match '^([\d.]+)/([\d.]+)ms$') { $p50 = [double]$Matches[1]; $p95 = [double]$Matches[2] }
  [pscustomobject]@{ Link = $Matches[1]; Consumer = $Matches[3]; In = [double]$Matches[4]; Out = [double]$Matches[5]; P50 = $p50; P95 = $p95; Thermal = $Matches[9]; Batt = $Matches[10] }
}
function Status-Between($fromIdx, $toIdx) {
  $all = Host-Lines
  if ($toIdx -gt $all.Count) { $toIdx = $all.Count }
  $r = @()
  for ($i = $fromIdx; $i -lt $toIdx; $i++) { $s = Parse-Status $all[$i]; if ($s) { $r += $s } }
  $r
}
function Parse-LogTime($line) {
  if ($line -match '^(\d\d):(\d\d):(\d\d)\.(\d\d\d) ') {
    $d = (Get-Date).Date
    return $d.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]).AddSeconds([int]$Matches[3]).AddMilliseconds([int]$Matches[4])
  }
  $null
}
function New-LogLines { if (Test-Path $logFile) { @(Get-Content $logFile -ErrorAction SilentlyContinue | Select-Object -Skip $logStartLines) } else { @() } }

function Start-HostProc {
  $script:hostProc = Start-Process -FilePath $hostExe -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr -WindowStyle Hidden -PassThru
  $script:bg += $script:hostProc
  Start-Sleep -Seconds 3
}
function Start-Probe($tag, $w, $h, $frames) {
  $json = Join-Path $outDir "probe-$tag.json"
  $log = Join-Path $outDir "probe-$tag.log"
  $p = Start-Process -FilePath $probeExe -ArgumentList @('--name', 'RaaiCam', '--width', $w, '--height', $h, '--frames', $frames, '--json', $json, '--expect', 'camera') `
    -RedirectStandardOutput $log -WindowStyle Hidden -PassThru
  $script:bg += $p
  @{ Proc = $p; Json = $json; Log = $log }
}
function Wait-Probe($pr, $timeoutMs) {
  if (-not $pr.Proc.WaitForExit($timeoutMs)) { $pr.Proc.Kill() }
  $script:lastProbeExit = Get-Date
  if (Test-Path $pr.Json) { Get-Content $pr.Json -Raw | ConvertFrom-Json }
  else { [pscustomobject]@{ opened = $false; fps = 0; frames = 0; width = 0; height = 0; cardFrames = 0; liveFrames = 0; otherFrames = 0; cameraFrames = 0; meanVariance = 0; error = 'no json produced' } }
}
function Fmt($j) { "{0}x{1} fps={2} frames={3} camera={4} card={5} live={6} other={7} meanVar={8} err={9}" -f $j.width, $j.height, $j.fps, $j.frames, $j.cameraFrames, $j.cardFrames, $j.liveFrames, $j.otherFrames, $j.meanVariance, $j.error }
function Percentile95($vals) {
  $s = @($vals | Sort-Object)
  if ($s.Count -eq 0) { return $null }
  $s[[Math]::Min($s.Count - 1, [int][Math]::Ceiling(0.95 * $s.Count) - 1)]
}

$p1Start = $null
try {
  Start-HostProc
  if ($script:hostProc.HasExited) { throw "raaicam-host exited immediately; see $hostErr" }

  # ---- P1: 1080p, >= 270 camera frames of 300, fps 28-32
  $i0 = (Host-Lines).Count
  $pr = Start-Probe 'P1' 1920 1080 300
  $p1 = Wait-Probe $pr 120000
  $i1 = (Host-Lines).Count
  $p1Status = Status-Between $i0 $i1
  $ok = $p1.opened -and $p1.cameraFrames -ge 270 -and (Fps-Ok $p1)
  Add-Result 'P1' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Fmt $p1)

  # ---- P2: 720p, same, and the host log shows the stream (re)started at 1280x720
  $logMark = (New-LogLines).Count
  $pr = Start-Probe 'P2' 1280 720 300
  $p2 = Wait-Probe $pr 120000
  $p2ProbeEnd = $script:lastProbeExit
  $newLog = @(New-LogLines | Select-Object -Skip $logMark)
  $restart = @($newLog | Where-Object { $_ -match '\[host\] (consumer size changed to 1280x720, restarting stream|start 1280x720)' })
  $ok = $p2.opened -and $p2.cameraFrames -ge 270 -and (Fps-Ok $p2) -and $restart.Count -ge 1
  Add-Result 'P2' $(if ($ok) { 'PASS' } else { 'FAIL' }) ((Fmt $p2) + "; host log 1280x720 (re)start lines=$($restart.Count)")

  # ---- P3: latency, p95 of the host's reported p95 values during P1 <= 100 ms
  $lat = @($p1Status | Where-Object { $null -ne $_.P95 } | ForEach-Object { $_.P95 })
  if ($lat.Count -lt 5) {
    Add-Result 'P3' 'FAIL' "only $($lat.Count) latency samples in the host status lines during P1 (need >= 5)"
  } else {
    $p95 = Percentile95 $lat
    $max = ($lat | Measure-Object -Maximum).Maximum
    Add-Result 'P3' $(if ($p95 -le 100) { 'PASS' } else { 'FAIL' }) ("p95 of reported p95 = $p95 ms (max $max ms, $($lat.Count) samples; limit 100 ms)")
  }

  # ---- P4: after the probe closed, the host sent stop within 1 s and the phone stopped streaming within 3 s.
  # The host does not expose the phone's streaming flag, so "phone stopped" = status line in=0.0 (no video arriving).
  $stopAt = $null; $inZeroAt = $null
  $deadline = (Get-Date).AddSeconds(8)
  while ((Get-Date) -lt $deadline -and (-not $stopAt -or -not $inZeroAt)) {
    if (-not $stopAt) {
      $hit = @(New-LogLines | Where-Object { $_ -match '\[host\] consumer inactive, stop' } | ForEach-Object { Parse-LogTime $_ } | Where-Object { $_ -and $_ -ge $p2ProbeEnd.AddSeconds(-1) })
      if ($hit.Count -gt 0) { $stopAt = $hit[0] }
    }
    if (-not $inZeroAt) {
      $last = Host-Lines | Where-Object { $_ -match '^link=' } | Select-Object -Last 1
      $s = if ($last) { Parse-Status $last } else { $null }
      if ($s -and $s.In -le 0.05 -and (Get-Date) -ge $p2ProbeEnd) { $inZeroAt = Get-Date }
    }
    Start-Sleep -Milliseconds 100
  }
  $stopMs = if ($stopAt) { [int]($stopAt - $p2ProbeEnd).TotalMilliseconds } else { $null }
  $zeroMs = if ($inZeroAt) { [int]($inZeroAt - $p2ProbeEnd).TotalMilliseconds } else { $null }
  # The status line prints once per second, so the "stopped" observation can be up to 1 s late: allow 3 s + 1 s.
  $ok = ($null -ne $stopMs) -and $stopMs -le 1000 -and ($null -ne $zeroMs) -and $zeroMs -le 4000
  Add-Result 'P4' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("probe closed -> host 'stop' logged after $(if ($null -ne $stopMs) { "$stopMs ms" } else { 'never' }) (limit 1000); status in=0.0 after $(if ($null -ne $zeroMs) { "$zeroMs ms" } else { 'never' }) (limit 3000 + 1000 status granularity)")

  # ---- P5: soak at 1080p
  if (-not $Soak) {
    Add-Result 'P5' 'SKIP' 'run with -Soak for the 10-minute soak'
  } else {
    $frames = 30 * 60 * $SoakMinutes
    $i0 = (Host-Lines).Count
    $pr = Start-Probe 'P5' 1920 1080 $frames
    $p5 = Wait-Probe $pr (($SoakMinutes + 3) * 60000)
    $st = @(Status-Between $i0 ((Host-Lines).Count) | Where-Object { $_.Consumer -ne 'inactive' })
    $steady = @($st | Select-Object -Skip 5)
    $badFps = @($steady | Where-Object { $_.Out -lt 28 -or $_.Out -gt 32 })
    $badPct = if ($steady.Count) { 100.0 * $badFps.Count / $steady.Count } else { 100.0 }
    $thermals = @($st | ForEach-Object { $_.Thermal } | Select-Object -Unique)
    $exc = @((Read-Shared $hostErr) + (New-LogLines | Where-Object { $_ -match '\[host\].*(Exception|unhandled)' }) + (Host-Lines | Where-Object { $_ -match 'Unhandled exception' }))
    $alive = -not $script:hostProc.HasExited
    $ok = $p5.opened -and (Fps-Ok $p5) -and $badPct -le 2 -and ($thermals -notcontains 'critical') -and $exc.Count -eq 0 -and $alive
    Add-Result 'P5' $(if ($ok) { 'PASS' } else { 'FAIL' }) ((Fmt $p5) + "; host out fps outside 28-32 in $($badFps.Count)/$($steady.Count) lines; thermal seen: $($thermals -join ',') ; host exceptions=$($exc.Count); host alive=$alive")
  }
}
catch {
  Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
  Add-Result 'ERROR' 'FAIL' $_.Exception.Message
}
finally {
  foreach ($p in $script:bg) { try { if ($p -and -not $p.HasExited) { $p.Kill() } } catch {} }
  Get-Process -Name 'raaicam-host', 'vcam-probe' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  try { New-LogLines | Set-Content (Join-Path $outDir 'host-log-excerpt.txt') -Encoding utf8 } catch {}
}

Write-Host ''
Write-Host '=== summary ==='
foreach ($k in $results.Keys) { Write-Host ("{0}: {1}" -f $k, $results[$k].Status) }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir 'summary.json') -Encoding utf8
if (($results.Values | Where-Object { $_.Status -eq 'FAIL' })) { exit 1 }
exit 0
