# RaaiCam Phase 0 spike: builds the virtual camera DLL, registrar and probe on the owner's PC
# (VS 2026 Build Tools at D:\VS\BuildTools, Windows SDK 10.0.26100 at D:\WindowsKits).
# Usage: powershell -ExecutionPolicy Bypass -File spikes\vcam\build.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $here 'build'

# The SDK is not in the default location; tell vcvars where it is before entering the dev shell.
$env:WindowsSdkDir = 'D:\WindowsKits\'
$env:WindowsSDKVersion = '10.0.26100.0\'
& 'D:\VS\BuildTools\Common7\Tools\Launch-VsDevShell.ps1' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$cmake = 'C:\Program Files\CMake\bin\cmake.exe'
& $cmake -S $here -B $build -G 'NMake Makefiles' -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $cmake --build $build
exit $LASTEXITCODE
