# RaaiCam Phase 0 spike: automated Step 6 checks (no GUI). Assumes the DLL is registered in HKLM
# (elevated regsvr32 once) and both projects are built. Outputs go to spikes\out (gitignored).
# Usage: powershell -ExecutionPolicy Bypass -File spikes\vcam\run-checks.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$bin = Join-Path $root 'spikes\vcam\build\bin'
$out = Join-Path $root 'spikes\out'
$writerDll = Join-Path $root 'spikes\framewriter\bin\Release\net10.0-windows\FrameWriter.dll'
New-Item -ItemType Directory -Force $out | Out-Null
function Stamp { (Get-Date).ToString('HH:mm:ss.fff') }

$reg = Start-Process "$bin\registrar.exe" -ArgumentList '--seconds', '600' -RedirectStandardOutput "$out\registrar.txt" -NoNewWindow -PassThru
Start-Sleep -Seconds 2
Get-Content "$out\registrar.txt"

try {
    Write-Output "=== A: writer absent (1280x720) ==="
    & "$bin\probe.exe" --frames 30 --width 1280 --height 720 --out "$out\A-absent.bmp" | Tee-Object "$out\A-absent.txt"

    Write-Output "=== A2: writer absent (1920x1080) ==="
    & "$bin\probe.exe" --frames 30 --width 1920 --height 1080 --out "$out\A2-absent-1080.bmp" | Tee-Object "$out\A2-absent-1080.txt"

    Write-Output "=== B: writer present ==="
    $w = Start-Process dotnet -ArgumentList $writerDll, '--wait', '5' -RedirectStandardOutput "$out\B-writer.txt" -NoNewWindow -PassThru
    Start-Sleep -Seconds 2
    & "$bin\probe.exe" --frames 60 --width 1280 --height 720 --out "$out\B-present.bmp" | Tee-Object "$out\B-present.txt"

    Write-Output "=== C: stale counter, writer start mid-stream, writer killed mid-stream ==="
    # writer from B is still running with no consumer; stop it, then open the camera again
    Stop-Process -Id $w.Id -Force
    Write-Output "$(Stamp) killed idle writer from B"
    Start-Sleep -Seconds 1
    $p = Start-Process "$bin\probe.exe" -ArgumentList '--frames', '300', '--width', '1280', '--height', '720', '--out', "$out\C-stopped.bmp" -RedirectStandardOutput "$out\C-probe.txt" -NoNewWindow -PassThru
    Start-Sleep -Seconds 3
    $w = Start-Process dotnet -ArgumentList $writerDll, '--wait', '5' -RedirectStandardOutput "$out\C-writer.txt" -NoNewWindow -PassThru
    Write-Output "$(Stamp) writer started"
    Start-Sleep -Seconds 4
    Stop-Process -Id $w.Id -Force
    Write-Output "$(Stamp) writer killed"
    $p.WaitForExit()
    Get-Content "$out\C-writer.txt"
    Get-Content "$out\C-probe.txt"

    Write-Output "=== D: two consumers at once (writer present) ==="
    $w = Start-Process dotnet -ArgumentList $writerDll, '--seconds', '12', '--wait', '5' -RedirectStandardOutput "$out\D-writer.txt" -NoNewWindow -PassThru
    Start-Sleep -Seconds 2
    $p1 = Start-Process "$bin\probe.exe" -ArgumentList '--frames', '120', '--width', '1280', '--height', '720', '--out', "$out\D-probe1.bmp" -RedirectStandardOutput "$out\D-probe1.txt" -NoNewWindow -PassThru
    Start-Sleep -Milliseconds 500
    $p2 = Start-Process "$bin\probe.exe" -ArgumentList '--frames', '60', '--width', '1280', '--height', '720', '--out', "$out\D-probe2.bmp" -RedirectStandardOutput "$out\D-probe2.txt" -NoNewWindow -PassThru
    $p1.WaitForExit(); $p2.WaitForExit()
    Write-Output "probe1 exit=$($p1.ExitCode) probe2 exit=$($p2.ExitCode)"
    Get-Content "$out\D-probe1.txt" | Select-String 'opened|TRANSITION|frames=|CLASSIFICATION|TIMEOUT|hr='
    Get-Content "$out\D-probe2.txt" | Select-String 'opened|TRANSITION|frames=|CLASSIFICATION|TIMEOUT|hr='
    $w.WaitForExit()
    Get-Content "$out\D-writer.txt" | Select-String 'consumerActive|opened|stopped'
}
finally {
    & "$bin\registrar.exe" --stop | Out-Null
    $reg.WaitForExit()
    Get-Content "$out\registrar.txt"
}
