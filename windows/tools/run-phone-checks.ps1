# RaaiCam phone end-to-end checks: real iPhone video into the virtual camera.
#   default    P1-P5 over USB (Phase 1B checks, now with the v2 paired link)
#   -WifiOnly  W1: cable UNPLUGGED, video over Wi-Fi only (run it with the cable out)
#   -Failover  F1: starts on USB with Wi-Fi standby, asks you to unplug and re-plug the cable, checks the switch
# Usage: powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1 [-Soak] [-SoakMinutes 10] [-WifiOnly] [-Failover]
# Needs: everything built (windows\build.ps1), the camera installed (windows\install\dev-install.ps1), the phone PAIRED with this PC
# (raaicam-host --pair), unlocked, with the RaaiCam app open on the "Ready" screen. Default and -Failover need the cable plugged in;
# -WifiOnly needs it unplugged. Phone and PC must be on the same Wi-Fi network (-WifiOnly, -Failover).
# NOTE: this script stops any running raaicam-host, raaicam-dev and vcam-probe processes before and after the checks.
# P5 (soak, default 10 minutes) only runs with -Soak, so the default run takes about 3 minutes.
# Exit codes: 0 all PASS (WARN/SKIP allowed), 1 a check failed, 2 preflight failed (nothing was run).
param([switch]$Soak, [int]$SoakMinutes = 10, [switch]$WifiOnly, [switch]$Failover)
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
if ($WifiOnly -and $Failover) { Fail-Preflight '-WifiOnly and -Failover were both given' 'Run them one at a time.' }
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

# Pairing: the host exits 3 (not paired) or 4 (pairing file unreadable) without touching any phone.
$null = (& $hostExe --check-paired | Out-String)
$pairCode = $LASTEXITCODE
if ($pairCode -eq 3) { Fail-Preflight 'this PC is not paired with a phone' 'Run raaicam-host --pair first (windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe --pair) and scan the code with the RaaiCam app. Then run this script again.' }
if ($pairCode -ne 0) { Fail-Preflight 'the pairing file could not be read' 'Close other programs that might use %APPDATA%\RaaiCam\pairing.bin, or run raaicam-host --pair again.' }

$dev = ""
try { $dev = (& $hostExe --list-devices | Out-String) } catch { $dev = "" }
$devCount = if ($dev -match 'devices=(\d+)') { [int]$Matches[1] } else { 0 }
$appState = if ($dev -match 'app=(\S+)') { $Matches[1] } else { 'n/a' }
if ($WifiOnly) {
  if ($devCount -ge 1) {
    Write-Host 'WARNING: an iPhone is still visible over USB. -WifiOnly needs the cable unplugged; the check will fail if video arrives over USB.' -ForegroundColor Yellow
  }
} else {
  if ($devCount -lt 1) {
    Fail-Preflight 'no iPhone is visible over USB' 'Plug the iPhone in with its cable, unlock it, tap "Trust This Computer" if asked, and make sure Apple Devices (or iTunes) is installed. Then run this script again.'
  }
  if ($appState -ne 'reachable') {
    Fail-Preflight 'the iPhone is connected but the RaaiCam app does not answer on port 47820' 'Unlock the iPhone and open the RaaiCam app (install it first with iloader if needed, see docs\checklists\phase-1b.md step 0). The screen should say "Ready". Then run this script again.'
  }
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$modeName = if ($WifiOnly) { 'wifi-only (W1)' } elseif ($Failover) { 'failover (F1)' } else { 'usb (P1-P5)' }
Say "preflight OK (camera present, paired, $devCount iPhone on USB); mode: $modeName; outputs: $outDir"

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
  $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } default { 'Gray' } }
  Write-Host ("[{0}] {1}  {2}" -f $id, $status, $detail) -ForegroundColor $color
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
# Parses the host status line (stable key=value tokens):
# "link=usb standby=wifi kbps=8000 phone=iPhone consumer=1920x1080 in=30.0 out=30.0 lat=48/71ms rtt=3ms rate=8.0Mbps thermal=nominal batt=82%+"
function Parse-Status($line) {
  if ($line -notmatch '^link=(?<link>\S+) standby=(?<standby>\S+) kbps=(?<kbps>\d+) phone=(?<phone>.*?) consumer=(?<consumer>\S+) in=(?<in>[\d.]+) out=(?<out>[\d.]+) lat=(?<lat>\S+) rtt=(?<rtt>\S+) rate=(?<rate>\S+) thermal=(?<thermal>\S+) batt=(?<batt>\S+)') { return $null }
  $m = $Matches  # the inner -match below would overwrite $Matches
  $p50 = $null; $p95 = $null
  if ($m['lat'] -match '^([\d.]+)/([\d.]+)ms$') { $p50 = [double]$Matches[1]; $p95 = [double]$Matches[2] }
  [pscustomobject]@{ Link = $m['link']; Standby = $m['standby']; Kbps = [int]$m['kbps']; Consumer = $m['consumer']; In = [double]$m['in']; Out = [double]$m['out']; P50 = $p50; P95 = $p95; Thermal = $m['thermal']; Batt = $m['batt'] }
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

# Status observations with the time this script first saw each line (the host status line carries no timestamp).
$script:statusSeen = 0
$script:obs = New-Object System.Collections.ArrayList
function Pump-Status {
  $all = Host-Lines
  for ($i = $script:statusSeen; $i -lt $all.Count; $i++) {
    $s = Parse-Status $all[$i]
    if ($s) { [void]$script:obs.Add([pscustomobject]@{ T = (Get-Date); S = $s; Raw = $all[$i] }) }
  }
  $script:statusSeen = $all.Count
}
# Waits until a status line observed after index $fromObs satisfies $pred; returns it, or $null on timeout.
function Wait-Status([scriptblock]$pred, [int]$timeoutSec, [int]$fromObs) {
  $deadline = (Get-Date).AddSeconds($timeoutSec)
  while ((Get-Date) -lt $deadline) {
    Pump-Status
    for ($i = $fromObs; $i -lt $script:obs.Count; $i++) { if (& $pred $script:obs[$i].S) { return $script:obs[$i] } }
    if ($script:hostProc -and $script:hostProc.HasExited) { return $null }
    Start-Sleep -Milliseconds 100
  }
  $null
}
# Prompt the owner and wait for Enter, still observing host status lines meanwhile.
function Wait-Enter($msg) {
  Write-Host ''
  Write-Host $msg -ForegroundColor Cyan
  try {
    while ($true) {
      while (-not [Console]::KeyAvailable) { Pump-Status; Start-Sleep -Milliseconds 100 }
      $k = [Console]::ReadKey($true)
      if ($k.Key -eq 'Enter') { break }
    }
  } catch { [void](Read-Host) }
  Pump-Status
}

# Observe host status lines for a fixed time (instead of sleeping blind).
function Pump-For([double]$sec) { $end = (Get-Date).AddSeconds($sec); while ((Get-Date) -lt $end) { Pump-Status; Start-Sleep -Milliseconds 100 }; Pump-Status }

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
function Median($vals) {
  $s = @($vals | Sort-Object)
  if ($s.Count -eq 0) { return $null }
  $s[[int][Math]::Floor(($s.Count - 1) / 2)]
}
# The probe prints "TRANSITION frame=N wall_ms=EPOCHMS -> LIVE|CARD|OTHER" whenever the frame class changes.
function Probe-Transitions($logPath) {
  $r = @()
  foreach ($l in (Read-Shared $logPath)) {
    if ($l -match 'TRANSITION frame=(\d+) wall_ms=(\d+) -> (\w+)') {
      $r += [pscustomobject]@{ Frame = [int]$Matches[1]; T = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$Matches[2]).LocalDateTime; Class = $Matches[3] }
    }
  }
  $r
}
# Card (placeholder) transitions after video was already flowing: [T] times, optionally limited to a time window.
function Card-Drops($trans, $from, $to) {
  $flowing = $false; $r = @()
  foreach ($t in $trans) {
    if ($t.Class -ne 'CARD') { $flowing = $true; continue }
    if ($flowing -and $t.T -ge $from -and $t.T -le $to) { $r += $t }
  }
  $r
}
function Stop-Everything {
  foreach ($p in $script:bg) { try { if ($p -and -not $p.HasExited) { $p.Kill() } } catch {} }
  Get-Process -Name 'raaicam-host', 'vcam-probe' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$p1Start = $null
try {
  Start-HostProc
  if ($script:hostProc.HasExited) {
    if ($script:hostProc.ExitCode -eq 3) { throw 'raaicam-host says no phone is paired: run raaicam-host --pair first' }
    throw "raaicam-host exited immediately (code $($script:hostProc.ExitCode)); see $hostErr"
  }

  if ($WifiOnly) {
    # ============================================================ W1: video over Wi-Fi only
    Pump-Status
    $up = Wait-Status { param($s) $s.Link -ne 'none' } 60 0
    if (-not $up) {
      Add-Result 'W1' 'FAIL' 'the host never connected to the phone within 60 s. Check: cable unplugged, iPhone unlocked with the RaaiCam app open, phone and PC on the SAME Wi-Fi, and Windows Firewall allowed raaicam-host on private networks.'
      throw 'W1 aborted: no link'
    }
    if ($up.S.Link -eq 'usb') {
      Add-Result 'W1' 'FAIL' 'the link is USB, not Wi-Fi. Unplug the cable (the host should switch to Wi-Fi by itself) and run this check again with the cable out.'
      throw 'W1 aborted: link is usb'
    }
    Add-Result 'W1a' 'PASS' "host link=$($up.S.Link) (Wi-Fi) reached"

    # 1080p
    $i0 = (Host-Lines).Count; $t0 = Get-Date
    $pr = Start-Probe 'W1-1080p' 1920 1080 300
    $w1 = Wait-Probe $pr 120000
    $i1 = (Host-Lines).Count
    $st = @(Status-Between $i0 $i1 | Where-Object { $_.Consumer -ne 'inactive' })
    $nonWifi = @($st | Where-Object { $_.Link -ne 'wifi' })
    $ok = $w1.opened -and $w1.cameraFrames -ge 270 -and (Fps-Ok $w1) -and $nonWifi.Count -eq 0
    Add-Result 'W1b' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("1080p over Wi-Fi: " + (Fmt $w1) + "; status lines with link other than wifi: $($nonWifi.Count)")

    # latency from the 1080p run: warn above 150 ms p95 (revisit trigger for TCP video)
    $p50s = @($st | Where-Object { $null -ne $_.P50 } | ForEach-Object { $_.P50 })
    $p95s = @($st | Where-Object { $null -ne $_.P95 } | ForEach-Object { $_.P95 })
    if ($p95s.Count -lt 5) {
      Add-Result 'W1c' 'FAIL' "only $($p95s.Count) latency samples in the host status lines (need >= 5)"
    } else {
      $m50 = Median $p50s; $q95 = Percentile95 $p95s; $mx = ($p95s | Measure-Object -Maximum).Maximum
      $lat = "latency over Wi-Fi: p50 (median) = $m50 ms, p95 = $q95 ms (max $mx ms, $($p95s.Count) samples)"
      if ($q95 -gt 150) { Add-Result 'W1c' 'WARN' ($lat + '; above 150 ms: tell Claude, TCP video over Wi-Fi may need revisiting') }
      else { Add-Result 'W1c' 'PASS' ($lat + '; limit 150 ms') }
    }

    # 720p
    $i0 = (Host-Lines).Count
    $pr = Start-Probe 'W1-720p' 1280 720 300
    $w2 = Wait-Probe $pr 120000
    $st2 = @(Status-Between $i0 ((Host-Lines).Count) | Where-Object { $_.Consumer -ne 'inactive' })
    $nonWifi2 = @($st2 | Where-Object { $_.Link -ne 'wifi' })
    $ok = $w2.opened -and $w2.cameraFrames -ge 270 -and (Fps-Ok $w2) -and $nonWifi2.Count -eq 0
    Add-Result 'W1d' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("720p over Wi-Fi: " + (Fmt $w2) + "; status lines with link other than wifi: $($nonWifi2.Count)")
  }
  elseif ($Failover) {
    # ============================================================ F1: unplug and re-plug during streaming
    $tl = New-Object System.Collections.ArrayList
    function TL($what) { $line = "{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $what; [void]$tl.Add($line); Write-Host "  $line" -ForegroundColor DarkGray }

    Pump-Status
    TL 'waiting for link=usb standby=wifi (USB streaming, Wi-Fi ready as backup)'
    $ready = Wait-Status { param($s) $s.Link -eq 'usb' -and $s.Standby -eq 'wifi' } 90 0
    if (-not $ready) {
      Add-Result 'F1' 'FAIL' 'never reached "link=usb standby=wifi" within 90 s. The Wi-Fi backup link did not come up: check the phone and PC are on the SAME Wi-Fi, and Windows Firewall allowed raaicam-host on private networks.'
      throw 'F1 aborted: no wifi standby'
    }
    TL 'link=usb standby=wifi reached'

    # one long probe across the whole test (killed at the end; the card check uses its transition log)
    $pr = Start-Probe 'F1' 1920 1080 18000
    $startedAt = Get-Date
    $live = Wait-Status { param($s) $s.Consumer -ne 'inactive' -and $s.In -ge 20 } 30 $script:obs.Count
    if (-not $live) {
      Add-Result 'F1' 'FAIL' 'no live video over USB before the unplug (status never showed in>=20 with an active camera)'
      throw 'F1 aborted: no video'
    }
    Start-Sleep -Seconds 3
    TL 'video is flowing over USB'

    # ---- unplug
    $promptAt = Get-Date
    $obsMark = $script:obs.Count
    Wait-Enter 'UNPLUG THE USB CABLE NOW, then press Enter.'
    TL 'owner pressed Enter (cable unplugged)'
    Pump-For 6
    $closed = @(New-LogLines | Where-Object { $_ -match '\[host\] phone link closed \(usb\)' } | ForEach-Object { Parse-LogTime $_ } | Where-Object { $_ -and $_ -ge $promptAt.AddSeconds(-1) })
    if ($closed.Count -eq 0) {
      Add-Result 'F1a' 'FAIL' 'the host never reported the USB link closing: was the cable really unplugged?'
    } else {
      $closeT = $closed[0]
      TL ("host log: usb link closed at {0:HH:mm:ss.fff}" -f $closeT)
      $after = @($script:obs | Select-Object -Skip $obsMark | Where-Object { $_.T -ge $closeT })
      if ($after.Count -eq 0) {
        Add-Result 'F1a' 'FAIL' 'no host status lines after the USB loss'
      } else {
        # Status lines stamped just after the loss can still be the pre-loss "link=usb" line; measure to the first one that is not usb.
        $post = @($after | Where-Object { $_.S.Link -ne 'usb' })
        if ($post.Count -eq 0) { $post = @($after | Select-Object -Last 1) }
        $first = $post[0]
        $afterFirst = @($after | Where-Object { $_.T -ge $first.T })
        $delay = ($first.T - $closeT).TotalSeconds
        $nones = @($afterFirst | Select-Object -First 6 | Where-Object { $_.S.Link -ne 'wifi' })
        TL ("first status after the loss: link={0} standby={1} seen {2:N2} s after the loss" -f $first.S.Link, $first.S.Standby, $delay)
        $ok = $first.S.Link -eq 'wifi' -and $delay -le 1.3 -and $nones.Count -eq 0
        Add-Result 'F1a' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("first status line after the cable loss: link=$($first.S.Link), $([Math]::Round($delay, 2)) s after the host noticed (limit 1.3 s incl. the 1 s status interval); of the next 6 status lines not on wifi: $($nones.Count)")
        $moving = @($afterFirst | Select-Object -Skip 1 -First 3 | Where-Object { $_.S.In -ge 15 })
        Add-Result 'F1b' $(if ($moving.Count -ge 2) { 'PASS' } else { 'FAIL' }) "video still arriving 2-5 s after the unplug (status in>=15 on $($moving.Count) of 3 lines)"
      }
    }
    $trans = Probe-Transitions $pr.Log
    $drops1 = @(Card-Drops $trans $promptAt.AddSeconds(-1) (Get-Date))
    Add-Result 'F1c' $(if ($drops1.Count -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($drops1.Count -eq 0) { 'no placeholder card frames in the camera during the switch to Wi-Fi' } else { "the camera showed the placeholder card $($drops1.Count) time(s) after the unplug (first at $($drops1[0].T.ToString('HH:mm:ss.fff')))" })

    # ---- plug back
    $promptAt2 = Get-Date
    $obsMark2 = $script:obs.Count
    $logMark2 = (New-LogLines).Count
    Wait-Enter 'PLUG THE USB CABLE BACK IN (keep the phone unlocked), then press Enter.'
    TL 'owner pressed Enter (cable plugged back in)'
    $back = Wait-Status { param($s) $s.Link -eq 'usb' -and $s.Standby -eq 'wifi' } 30 $obsMark2
    $warm = @(New-LogLines | Select-Object -Skip $logMark2 | Where-Object { $_ -match '\[host\] usb is back' } | ForEach-Object { Parse-LogTime $_ } | Where-Object { $_ })
    if (-not $back) {
      Add-Result 'F1d' 'FAIL' 'link=usb standby=wifi did not come back within 30 s of plugging in. Check the cable, that the phone is unlocked and RaaiCam is open.'
    } else {
      if ($warm.Count -eq 0) {
        TL 'link=usb standby=wifi seen, but the host log has no "usb is back" line to time it from'
        Add-Result 'F1d' 'WARN' 'link=usb standby=wifi came back, but the host log has no "usb is back" line, so the 10 s limit could not be timed. Tell Claude.'
      } else {
        $took = ($back.T - $warm[0]).TotalSeconds
        TL ("link=usb standby=wifi seen {0:N1} s after the host saw USB come back" -f $took)
        Add-Result 'F1d' $(if ($took -le 10) { 'PASS' } else { 'FAIL' }) "link=usb standby=wifi again $([Math]::Round($took, 1)) s after USB came back (limit 10 s)"
      }
    }
    Pump-For 4
    $trans = Probe-Transitions $pr.Log
    $drops2 = @(Card-Drops $trans $promptAt2.AddSeconds(-1) (Get-Date))
    Add-Result 'F1e' $(if ($drops2.Count -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($drops2.Count -eq 0) { 'no placeholder card frames in the camera during the switch back to USB' } else { "the camera showed the placeholder card $($drops2.Count) time(s) while switching back to USB" })
    $tailFps = @($script:obs | Select-Object -Last 3 | ForEach-Object { $_.S.Out })
    Add-Result 'F1f' $(if (@($tailFps | Where-Object { $_ -ge 25 }).Count -ge 2) { 'PASS' } else { 'FAIL' }) "video still flowing at the end (out fps of the last 3 status lines: $($tailFps -join ', '))"
    $tl | Set-Content (Join-Path $outDir 'failover-timeline.txt') -Encoding utf8
    $script:obs | ForEach-Object { "{0:HH:mm:ss.fff} {1}" -f $_.T, $_.Raw } | Set-Content (Join-Path $outDir 'failover-status-observed.txt') -Encoding utf8
  }
  else {
    # ============================================================ P1-P5 over USB
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
}
catch {
  Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
  Add-Result 'ERROR' 'FAIL' $_.Exception.Message
}
finally {
  Stop-Everything
  try { New-LogLines | Set-Content (Join-Path $outDir 'host-log-excerpt.txt') -Encoding utf8 } catch {}
}

Write-Host ''
Write-Host '=== summary ==='
foreach ($k in $results.Keys) { Write-Host ("{0}: {1}" -f $k, $results[$k].Status) }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir 'summary.json') -Encoding utf8
if (($results.Values | Where-Object { $_.Status -eq 'FAIL' })) { exit 1 }
exit 0
