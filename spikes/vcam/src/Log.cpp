// RaaiCam Phase 0 spike: see Log.h.
#include <windows.h>
#include <strsafe.h>
#include "Log.h"

static const wchar_t kLogDir[] = L"C:\\ProgramData\\RaaiCamSpike";
static const wchar_t kLogPath[] = L"C:\\ProgramData\\RaaiCamSpike\\vcam.log";
static const LONGLONG kMaxLogBytes = 8 * 1024 * 1024;

static void AppendToFile(const wchar_t* line)
{
	CreateDirectoryW(kLogDir, nullptr); // ignore failure (exists, or no rights)
	auto file = CreateFileW(kLogPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
	if (file == INVALID_HANDLE_VALUE)
		return;

	LARGE_INTEGER size{};
	if (GetFileSizeEx(file, &size) && size.QuadPart < kMaxLogBytes)
	{
		char utf8[4096];
		auto len = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
		if (len > 1)
		{
			DWORD written = 0;
			WriteFile(file, utf8, (DWORD)(len - 1), &written, nullptr);
		}
	}
	CloseHandle(file);
}

void VLog(PCWSTR format, ...)
{
	wchar_t msg[1536];
	va_list args;
	va_start(args, format);
	StringCchVPrintfW(msg, _countof(msg), format, args);
	va_end(args);

	SYSTEMTIME st;
	GetLocalTime(&st);
	DWORD session = 0;
	ProcessIdToSessionId(GetCurrentProcessId(), &session);

	wchar_t line[2048];
	StringCchPrintfW(line, _countof(line), L"%02u:%02u:%02u.%03u pid=%lu sess=%lu tid=%lu [RaaiCamVCam] %s\r\n",
		st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), session, GetCurrentThreadId(), msg);

	OutputDebugStringW(line);
	AppendToFile(line);
}

void VLogWin32(PCWSTR what, DWORD error)
{
	wchar_t text[512] = L"";
	FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0, text, _countof(text), nullptr);
	// trim trailing CR/LF
	for (auto len = wcslen(text); len > 0 && (text[len - 1] == L'\r' || text[len - 1] == L'\n'); len--)
		text[len - 1] = 0;
	VLog(L"%s failed: Win32 error %lu (%s)", what, error, text);
}
