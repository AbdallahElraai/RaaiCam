// RaaiCam Phase 0 spike: diagnostics for code running inside the Windows Frame Server service.
// Every line goes to OutputDebugStringW (prefix "[RaaiCamVCam]") and is appended to
// C:\ProgramData\RaaiCamSpike\vcam.log (folder must be writable by LocalService).
#pragma once

#include <windows.h>

void VLog(PCWSTR format, ...);

// Logs "<what> failed: Win32 error <n> (<message>)" using the given error code.
void VLogWin32(PCWSTR what, DWORD error);
