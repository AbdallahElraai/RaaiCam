# RaaiCam Windows build: native virtual camera (CMake/NMake) + .NET solution.
# Usage: powershell -ExecutionPolicy Bypass -File windows\build.ps1 [-Test]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$vcam = Join-Path $here 'RaaiCam.VirtualCam'
$build = Join-Path $vcam 'build'

# The SDK is not in the default location; tell vcvars where it is before entering the dev shell.
$env:WindowsSdkDir = 'D:\WindowsKits\'
$env:WindowsSDKVersion = '10.0.26100.0\'
& 'D:\VS\BuildTools\Common7\Tools\Launch-VsDevShell.ps1' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$cmake = 'C:\Program Files\CMake\bin\cmake.exe'
& $cmake -S $vcam -B $build -G 'NMake Makefiles' -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $cmake --build $build
if ($LASTEXITCODE) { exit $LASTEXITCODE }

dotnet build (Join-Path $here 'RaaiCam.sln') -c Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($Test) {
  & (Join-Path $build 'bin\vcam_tests.exe')
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  dotnet test (Join-Path $here 'RaaiCam.Media.Tests') -c Release --no-build
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  dotnet test (Join-Path $here 'RaaiCam.Protocol.Tests') -c Release --no-build
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
exit 0
