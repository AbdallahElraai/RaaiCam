# RaaiCam DEV installer: copies RaaiCamVCam.dll to an admin-only folder, registers it, records the allowed user SID,
# prepares the log folder, then creates the camera as the NON-elevated user (the camera is Access_CurrentUser).
#
# SECURITY NOTE: this is a development installer. It elevates code taken from the user-writable repo (this script and
# build\bin\RaaiCamVCam.dll), so malware running as the same user could tamper with them before you approve UAC.
# The production installer (Phase 1C) must ship signed binaries from an admin-only source.
#
# Run: powershell -ExecutionPolicy Bypass -File windows\install\dev-install.ps1 [-UserSid S-1-5-21-...]
# Exit codes: 0 ok, 1 error, 2 bad -UserSid, 3 DLL locked (try `Restart-Service FrameServer` elevated),
#   4 unsafe data/log folder (reparse point or owned by a non-admin), 5 program folder not admin-only,
#   6 regsvr32 failed, 7 icacls failed. The elevated child reports only through its exit code (no files in %TEMP%).
param([string]$UserSid, [switch]$ElevatedPart)
$ErrorActionPreference = 'Stop'

$bin = Join-Path $PSScriptRoot '..\RaaiCam.VirtualCam\build\bin'
$vcamctl = Join-Path $bin 'vcamctl.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Say([string]$m) { Write-Host $m }

function Test-Sid([string]$s) {
  if ($s -notmatch '^S-1-\d+(-\d+)+$') { return $false }
  try { $null = New-Object System.Security.Principal.SecurityIdentifier($s); return $true } catch { return $false }
}

function Get-TargetSid {
  $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  if (-not $sid) {
    $session = (Get-Process -Id $PID).SessionId
    $p = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { $_.SessionId -eq $session } | Select-Object -First 1
    if ($p) { $sid = (Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid).Sid }
  }
  $sid
}

# True when only Administrators / SYSTEM / TrustedInstaller / CREATOR OWNER (plus $extraOk) hold write-type allow ACEs on $dir.
function Test-AdminOnly([string]$dir, [string[]]$extraOk = @()) {
  $okSids = $extraOk + @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', 'S-1-3-0')
  $write = [System.Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions, TakeOwnership'
  foreach ($r in (Get-Acl $dir).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
    if ($r.AccessControlType -ne 'Allow') { continue }
    if (($r.FileSystemRights -band $write) -and ($okSids -notcontains $r.IdentityReference.Value)) { return $false }
  }
  return $true
}

# True when $path is owned by Administrators, SYSTEM or TrustedInstaller.
function Test-AdminOwned([string]$path) {
  $owner = (Get-Acl $path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value
  @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') -contains $owner
}

function Test-Reparse([string]$path) {
  (Test-Path $path) -and ((Get-Item $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Install-Elevated([string]$sid) {
  $src = (Resolve-Path (Join-Path $bin 'RaaiCamVCam.dll')).Path
  $destDir = Join-Path $env:ProgramFiles 'RaaiCam\VirtualCam'
  $dest = Join-Path $destDir 'RaaiCamVCam.dll'
  $dataDir = Join-Path $env:ProgramData 'RaaiCam'
  $logs = Join-Path $dataDir 'logs'

  # 1. Validate what already exists, BEFORE copying or registering anything.
  foreach ($d in @($dataDir, $logs)) {
    if (-not (Test-Path $d)) { continue }
    if (Test-Reparse $d) { Say "ERROR: $d is a reparse point; refusing to use it."; return 4 }
    if (-not (Test-AdminOwned $d)) { Say "ERROR: $d is owned by a non-administrator; refusing to use it. Delete it from an elevated shell and rerun."; return 4 }
  }
  New-Item -ItemType Directory -Force $destDir | Out-Null
  if (-not (Test-AdminOnly $destDir)) { Say "ERROR: $destDir is writable by non-administrators; not installing."; return 5 }

  # 2. Lock down %ProgramData%\RaaiCam (SYSTEM + Administrators full, LocalService read/execute), then logs
  #    (LocalService modify: the Frame Server writes its log there).
  New-Item -ItemType Directory -Force $dataDir | Out-Null
  & icacls.exe $dataDir /setowner '*S-1-5-32-544' | Out-Null
  if ($LASTEXITCODE) { Say "ERROR: icacls setowner failed ($LASTEXITCODE)"; return 7 }
  & icacls.exe $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' | Out-Null
  if ($LASTEXITCODE) { Say "ERROR: icacls failed ($LASTEXITCODE)"; return 7 }
  New-Item -ItemType Directory -Force $logs | Out-Null
  foreach ($d in @($dataDir, $logs)) {
    if (Test-Reparse $d) { Say "ERROR: $d is a reparse point; refusing to use it."; return 4 }
  }
  & icacls.exe $logs /setowner '*S-1-5-32-544' | Out-Null
  if ($LASTEXITCODE) { Say "ERROR: icacls setowner failed ($LASTEXITCODE)"; return 7 }
  & icacls.exe $logs /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
  if ($LASTEXITCODE) { Say "ERROR: icacls failed ($LASTEXITCODE)"; return 7 }
  foreach ($d in @($dataDir, $logs)) {
    if (-not (Test-AdminOnly $d @('S-1-5-19'))) { Say "ERROR: $d is still writable by non-administrators after lockdown."; return 4 }
  }

  # 3. Copy, register, record the allowed user.
  $copied = $false
  for ($i = 0; $i -lt 30 -and -not $copied; $i++) {
    try { Copy-Item $src $dest -Force -ErrorAction Stop; $copied = $true }
    catch {
      $hr = $_.Exception.HResult -band 0xFFFF
      if ($hr -ne 32 -and $hr -ne 33) { Say "ERROR: copy failed: $_"; return 1 }   # only sharing violations are retried
      Say "DLL locked, retrying ($($i + 1)/30)..."; Start-Sleep -Seconds 2
    }
  }
  if (-not $copied) {
    Say "ERROR: $dest is still locked (Frame Server). From an elevated shell run: Restart-Service FrameServer, then rerun this script."
    return 3
  }

  & "$env:SystemRoot\System32\regsvr32.exe" /s $dest
  if ($LASTEXITCODE) { Say "ERROR: regsvr32 failed ($LASTEXITCODE)"; return 6 }

  $key = 'HKLM:\SOFTWARE\RaaiCam\VirtualCam'
  New-Item -Path $key -Force | Out-Null
  New-ItemProperty -Path $key -Name AllowedUserSid -Value $sid -PropertyType String -Force | Out-Null
  Say "Installed:  $dest"
  Say "Registered: HKLM CLSID InprocServer32 (regsvr32)"
  Say "AllowedUserSid: $sid"
  Say "Log folder: $logs"
  return 0
}

if ($ElevatedPart) {
  if (-not (Test-Sid $UserSid)) { Say "ERROR: invalid -UserSid '$UserSid'"; exit 2 }
  try { $rc = Install-Elevated $UserSid } catch { Say "ERROR: $_"; $rc = 1 }
  exit $rc
}

if (-not $UserSid) { $UserSid = Get-TargetSid }   # resolved BEFORE elevation, in the non-elevated process
if (-not $UserSid) { Write-Host 'ERROR: could not determine the user SID; pass -UserSid'; exit 2 }
if (-not (Test-Sid $UserSid)) { Write-Host "ERROR: invalid -UserSid '$UserSid' (expected S-1-...)"; exit 2 }
foreach ($f in @('RaaiCamVCam.dll', 'vcamctl.exe')) {
  if (-not (Test-Path (Join-Path $bin $f))) { Write-Host "ERROR: $f not built; run windows\build.ps1 first"; exit 1 }
}

if ($isAdmin) {
  $rc = Install-Elevated $UserSid
  if ($rc) { exit $rc }
  Write-Host 'Elevated shell: now run, in a NON-elevated shell:'
  Write-Host "  $vcamctl create"
  exit 0
}

$argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ElevatedPart -UserSid `"$UserSid`""
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Write-Host 'Requesting elevation (approve the UAC prompt)...'
try { $p = Start-Process $ps -Verb RunAs -ArgumentList $argList -Wait -PassThru }
catch { Write-Host "ERROR: elevation declined or failed: $_"; exit 1 }
$reasons = @{ 1 = 'error'; 2 = 'bad user SID'; 3 = 'installed DLL is locked by Frame Server (elevated: Restart-Service FrameServer, then rerun)';
  4 = '%ProgramData%\RaaiCam is unsafe (reparse point or owned by a non-administrator)'; 5 = 'the program folder is not admin-only';
  6 = 'regsvr32 failed'; 7 = 'icacls failed' }
if ($p.ExitCode) { Write-Host "Elevated install failed (exit $($p.ExitCode)): $($reasons[[int]$p.ExitCode]). Re-run from an elevated shell to see details."; exit $p.ExitCode }

& $vcamctl create
exit $LASTEXITCODE
