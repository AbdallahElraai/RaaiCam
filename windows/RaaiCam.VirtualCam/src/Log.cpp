// RaaiCam virtual camera diagnostics: see Log.h.
#include <windows.h>
#include <strsafe.h>
#include <cstdarg>
#include <cstdlib>
#include <cwchar>

#include "Log.h"

namespace {

constexpr LONGLONG kMaxLogBytes = 8 * 1024 * 1024;
constexpr wchar_t kLogPathTemplate[] = L"%ProgramData%\\RaaiCam\\logs\\vcam.log";
constexpr wchar_t kPrefix[] = L"[RaaiCamVCam] ";

// Constant-initialized and trivially destructible: no dynamic initializer and no destructor at DLL
// load/unload, so nothing runs under the loader lock.
SRWLOCK g_logLock = SRWLOCK_INIT;
HANDLE g_logFile = INVALID_HANDLE_VALUE;
bool g_logOpenTried = false;
LONGLONG g_logBytes = 0;

// Caller holds g_logLock.
void OpenLogLocked() {
  g_logOpenTried = true;
  wchar_t path[MAX_PATH];
  const DWORD n = ExpandEnvironmentStringsW(kLogPathTemplate, path, MAX_PATH);
  if (n == 0 || n > MAX_PATH) return;
  // The folder is created by the installer with the right ACL; never create it here.
  HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                            OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return;  // OutputDebugString only
  LARGE_INTEGER size{};
  g_logBytes = GetFileSizeEx(file, &size) ? size.QuadPart : 0;
  g_logFile = file;
}

}  // namespace

void VLog(PCWSTR format, ...) {
  wchar_t msg[1536];
  va_list args;
  va_start(args, format);
  StringCchVPrintfW(msg, _countof(msg), format, args);
  va_end(args);

  SYSTEMTIME st;
  GetLocalTime(&st);
  wchar_t line[2048];
  StringCchPrintfW(line, _countof(line), L"%s%04u-%02u-%02u %02u:%02u:%02u.%03u pid=%lu tid=%lu %s\r\n", kPrefix,
                   st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                   GetCurrentProcessId(), GetCurrentThreadId(), msg);
  OutputDebugStringW(line);

  char utf8[4096];
  const int len = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
  if (len <= 1) return;

  AcquireSRWLockExclusive(&g_logLock);
  if (!g_logOpenTried) OpenLogLocked();
  if (g_logFile != INVALID_HANDLE_VALUE && g_logBytes < kMaxLogBytes) {
    DWORD written = 0;
    if (WriteFile(g_logFile, utf8, DWORD(len - 1), &written, nullptr)) g_logBytes += written;
  }
  ReleaseSRWLockExclusive(&g_logLock);
}

void VLogWin32(PCWSTR what, DWORD error) {
  wchar_t text[512] = L"";
  FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0, text, _countof(text),
                 nullptr);
  for (size_t len = wcslen(text); len > 0 && (text[len - 1] == L'\r' || text[len - 1] == L'\n'); len--) {
    text[len - 1] = 0;
  }
  VLog(L"%s failed: Win32 error %lu (%s)", what, error, text);
}

void VLogClose() {
  AcquireSRWLockExclusive(&g_logLock);
  if (g_logFile != INVALID_HANDLE_VALUE) CloseHandle(g_logFile);
  g_logFile = INVALID_HANDLE_VALUE;
  g_logOpenTried = false;
  g_logBytes = 0;
  ReleaseSRWLockExclusive(&g_logLock);
}
