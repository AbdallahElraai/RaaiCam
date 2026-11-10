# Phase 0 — Development environment

Recorded 2026-11-10 on the owner's PC (Windows 11, build 10.0.26200).

| Tool | Version | Location | Notes |
|---|---|---|---|
| Git | 2.53.0 | system | already installed |
| GitHub CLI | 2.96.0 | system | logged in, scopes include `repo`, `workflow` |
| .NET SDK | 10.0.403 | `C:\Program Files\dotnet` | winget `Microsoft.DotNet.SDK.10` |
| CMake | 4.4.4 | `C:\Program Files\CMake\bin` | winget `Kitware.CMake`; open a new shell for PATH |
| MSVC (VS 2026 Build Tools 18.11) | VC.Tools.x86.x64 only | `D:\VS\BuildTools` | VS 2022 Build Tools bootstrapper failed (outdated installer feed) |
| Windows SDK | 10.0.26100 (Desktop C++ x64) | `D:\WindowsKits` | separate install; contains `mfvirtualcamera.h` |
| Apple Devices | Microsoft Store `9NP83LWLPZ9K` | Store app | usbmux (`127.0.0.1:27015`) not listening until the app has run once; open Apple Devices and connect the iPhone |

## Disk space constraint
C: had only ~2.6 GB free, so the full "Desktop development with C++" workload (6.7 GB) could not be installed. The compiler and Windows SDK were installed to D: instead. CMake must be told where the SDK is, e.g. via a VS Developer environment:

```powershell
& "D:\VS\BuildTools\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64
$env:WindowsSdkDir = "D:\WindowsKits\"
```

(Task 2 records the exact working CMake invocation in its report.)
