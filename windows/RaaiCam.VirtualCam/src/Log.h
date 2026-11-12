// RaaiCam virtual camera diagnostics. The DLL runs inside the shared Frame Server service, so:
// - nothing here runs from DllMain: the log file is opened lazily on the first VLog call;
// - every line goes to OutputDebugStringW and, when it could be opened, is appended to
//   %ProgramData%\RaaiCam\logs\vcam.log (the installer creates the folder: LocalService modify, Users none);
// - every line starts exactly with "[RaaiCamVCam] ";
// - callers log state transitions only, never per frame.
// VLog takes only its own internal lock (a leaf lock: it never calls out while holding it).
#pragma once

#include <windows.h>

void VLog(PCWSTR format, ...);

// Logs "<what> failed: Win32 error <n> (<message>)".
void VLogWin32(PCWSTR what, DWORD error);

// Closes the log file (reopened lazily by the next VLog). Called from DllCanUnloadNow, never from DllMain.
void VLogClose();
