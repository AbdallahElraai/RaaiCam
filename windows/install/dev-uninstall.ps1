# RaaiCam dev uninstall: removes the camera (non-elevated), then unregisters the DLL and deletes it (elevated).
# Run: powershell -ExecutionPolicy Bypass -File windows\install\dev-uninstall.ps1
# The elevated child reports only through its exit code (no files in %TEMP%).
# Exit codes: 0 ok, 1 error, 2 camera could not be removed / is still present, 3 DLL locked (try `Restart-Service FrameServer` from an elevated shell, then rerun).
param([switch]$ElevatedPart)
$ErrorActionPreference = 'Stop'

$vcamctl = Join-Path $PSScriptRoot '..\RaaiCam.VirtualCam\build\bin\vcamctl.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$destDir = Join-Path $env:ProgramFiles 'RaaiCam\VirtualCam'
$dest = Join-Path $destDir 'RaaiCamVCam.dll'

function Say([string]$m) { Write-Host $m }

function Uninstall-Elevated {
  if (Test-Path $dest) {
    & "$env:SystemRoot\System32\regsvr32.exe" /u /s $dest
    if ($LASTEXITCODE) { Say "WARNING: regsvr32 /u returned $LASTEXITCODE" }
  }
  Remove-Item 'HKLM:\SOFTWARE\RaaiCam\VirtualCam' -Recurse -Force -ErrorAction SilentlyContinue
  if (Test-Path $destDir) {
    $gone = $false
    for ($i = 0; $i -lt 30 -and -not $gone; $i++) {
      try { Remove-Item $destDir -Recurse -Force -ErrorAction Stop; $gone = $true }
      catch {
        $hr = $_.Exception.HResult -band 0xFFFF
        if ($hr -ne 32 -and $hr -ne 33) { Say "ERROR: delete failed: $_"; return 1 }   # only sharing violations are retried
        Say "DLL locked, retrying ($($i + 1)/30)..."; Start-Sleep -Seconds 2
      }
    }
    if (-not $gone) {
      Say "ERROR: $destDir is still locked (Frame Server). From an elevated shell run: Restart-Service FrameServer, then rerun this script."
      return 3
    }
  }
  Say 'Unregistered and removed the installed DLL.'
  return 0
}

if ($ElevatedPart) {
  try { $rc = Uninstall-Elevated } catch { Say "ERROR: $_"; $rc = 1 }
  exit $rc
}

# Camera removal first; Access_CurrentUser means it must run non-elevated.
if (Test-Path $vcamctl) {
  if ($isAdmin) {
    # The camera is Access_CurrentUser and can only be removed non-elevated. Deleting the DLL under a live camera
    # would leave a registered device with no source, so refuse unless the camera is already gone.
    $status = (& $vcamctl status) -join ''
    if ($status -match '"cameraPresent"\s*:\s*false') { Write-Host 'Camera already removed; continuing with the DLL.' }
    else {
      Write-Host 'ERROR: the virtual camera still exists, and it can only be removed from a normal (non-elevated) shell.'
      Write-Host 'Run this script from a normal (non-elevated) PowerShell window; it will ask for UAC itself. Nothing was deleted.'
      exit 2
    }
  } else {
    & $vcamctl remove
    if ($LASTEXITCODE) { Write-Host "ERROR: vcamctl remove failed (exit $LASTEXITCODE); not deleting the DLL. Fix that and rerun."; exit 2 }
  }
} else { Write-Host 'ERROR: vcamctl.exe not found; cannot remove the camera, so not deleting the DLL. Run windows\build.ps1 and rerun.'; exit 2 }

if ($isAdmin) {
  $rc = Uninstall-Elevated
  exit $rc
}

$argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ElevatedPart"
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Write-Host 'Requesting elevation (approve the UAC prompt)...'
try { $p = Start-Process $ps -Verb RunAs -ArgumentList $argList -Wait -PassThru }
catch { Write-Host "ERROR: elevation declined or failed: $_"; exit 1 }
if ($p.ExitCode -eq 3) { Write-Host 'DLL still locked by Frame Server: from an elevated shell run Restart-Service FrameServer, then rerun.' }
elseif ($p.ExitCode) { Write-Host "Elevated uninstall failed (exit $($p.ExitCode)). Re-run from an elevated shell to see details." }
exit $p.ExitCode
