// RaaiCam Phase 0 spike: registers the "RaaiCam Test" virtual camera for the lifetime of this process.
//
//   registrar.exe [--register-dll] [--unregister-dll] [--seconds N]
//   registrar.exe --stop          signals a running registrar to remove the camera and exit
//
// --register-dll    writes HKCU\Software\Classes\CLSID\{CLSID}\InprocServer32 = <dir>\RaaiCamVCam.dll
//                   (regsvr32-equivalent, per user, no elevation). Note: Frame Server runs as
//                   LocalService and may only see HKLM; see docs/spikes/phase0-results.md.
// --unregister-dll  deletes that HKCU key and exits.
// --seconds N       remove the camera after N seconds (default: wait for Enter or --stop).
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <cstdio>
#include <string>
#include <thread>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfsensorgroup.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")

static const wchar_t kClsid[] = L"{7EC2CD0F-93C4-4E06-BF91-63F3B541C1E4}";
static const wchar_t kFriendlyName[] = L"RaaiCam Test";
static const wchar_t kStopEvent[] = L"Local\\RaaiCamSpikeRegistrarStop";

static std::wstring DllPath()
{
	wchar_t exe[MAX_PATH];
	GetModuleFileNameW(nullptr, exe, MAX_PATH);
	std::wstring path(exe);
	path = path.substr(0, path.find_last_of(L'\\') + 1);
	return path + L"RaaiCamVCam.dll";
}

static int RegisterDllHkcu()
{
	auto dll = DllPath();
	if (GetFileAttributesW(dll.c_str()) == INVALID_FILE_ATTRIBUTES)
	{
		wprintf(L"ERROR: %s not found\n", dll.c_str());
		return 2;
	}

	std::wstring key = std::wstring(L"Software\\Classes\\CLSID\\") + kClsid + L"\\InprocServer32";
	HKEY hkey = nullptr;
	auto err = RegCreateKeyExW(HKEY_CURRENT_USER, key.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &hkey, nullptr);
	if (err == ERROR_SUCCESS)
	{
		err = RegSetValueExW(hkey, nullptr, 0, REG_SZ, (const BYTE*)dll.c_str(), (DWORD)((dll.size() + 1) * sizeof(wchar_t)));
		if (err == ERROR_SUCCESS)
		{
			static const wchar_t both[] = L"Both";
			err = RegSetValueExW(hkey, L"ThreadingModel", 0, REG_SZ, (const BYTE*)both, sizeof(both));
		}
		RegCloseKey(hkey);
	}

	if (err != ERROR_SUCCESS)
	{
		wprintf(L"ERROR: HKCU registration failed, Win32 error %ld\n", err);
		return 2;
	}
	wprintf(L"DLL registered (HKCU): %s\n", dll.c_str());
	return 0;
}

static int UnregisterDllHkcu()
{
	std::wstring key = std::wstring(L"Software\\Classes\\CLSID\\") + kClsid;
	auto err = RegDeleteTreeW(HKEY_CURRENT_USER, key.c_str());
	wprintf(L"HKCU unregister: Win32 error %ld\n", err);
	return err == ERROR_SUCCESS || err == ERROR_FILE_NOT_FOUND ? 0 : 2;
}

int wmain(int argc, wchar_t** argv)
{
	setvbuf(stdout, nullptr, _IONBF, 0);
	bool registerDll = false;
	int seconds = 0;
	for (int i = 1; i < argc; i++)
	{
		std::wstring a = argv[i];
		if (a == L"--register-dll")
			registerDll = true;
		else if (a == L"--unregister-dll")
			return UnregisterDllHkcu();
		else if (a == L"--seconds" && i + 1 < argc)
			seconds = _wtoi(argv[++i]);
		else if (a == L"--stop")
		{
			auto h = OpenEventW(EVENT_MODIFY_STATE, FALSE, kStopEvent);
			if (!h)
			{
				wprintf(L"no running registrar (Win32 error %lu)\n", GetLastError());
				return 1;
			}
			SetEvent(h);
			CloseHandle(h);
			wprintf(L"stop signalled\n");
			return 0;
		}
		else
		{
			wprintf(L"usage: registrar.exe [--register-dll] [--unregister-dll] [--seconds N] | --stop\n");
			return 1;
		}
	}

	if (registerDll)
	{
		auto rc = RegisterDllHkcu();
		if (rc)
			return rc;
	}

	auto stopEvent = CreateEventW(nullptr, TRUE, FALSE, kStopEvent);

	auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	if (SUCCEEDED(hr))
		hr = MFStartup(MF_VERSION);
	if (FAILED(hr))
	{
		wprintf(L"ERROR: COM/MF startup failed hr=0x%08X\n", hr);
		return 2;
	}

	IMFVirtualCamera* cam = nullptr;
	hr = MFCreateVirtualCamera(
		MFVirtualCameraType_SoftwareCameraSource,
		MFVirtualCameraLifetime_Session,
		MFVirtualCameraAccess_CurrentUser,
		kFriendlyName,
		kClsid,
		nullptr, 0,
		&cam);
	if (FAILED(hr))
	{
		wprintf(L"ERROR: MFCreateVirtualCamera hr=0x%08X\n", hr);
		MFShutdown();
		return 2;
	}

	hr = cam->Start(nullptr);
	if (FAILED(hr))
	{
		wprintf(L"ERROR: IMFVirtualCamera::Start hr=0x%08X\n", hr);
		cam->Remove();
		cam->Release();
		MFShutdown();
		return 2;
	}

	wprintf(L"REGISTERED \"%s\" %s\n", kFriendlyName, kClsid);

	// Enter on a real console stops; EOF on stdin (background run) is ignored.
	std::thread([stopEvent]
		{
			wchar_t line[64];
			if (fgetws(line, 64, stdin))
				SetEvent(stopEvent);
		}).detach();

	if (seconds > 0)
		wprintf(L"running for %d s (or until Enter / registrar.exe --stop)\n", seconds);
	else
		wprintf(L"press Enter (or run registrar.exe --stop) to remove the camera\n");
	WaitForSingleObject(stopEvent, seconds > 0 ? (DWORD)seconds * 1000 : INFINITE);

	hr = cam->Remove();
	wprintf(hr == S_OK ? L"REMOVED\n" : L"Remove hr=0x%08X\n", hr);
	cam->Release();
	MFShutdown();
	CoUninitialize();
	return 0;
}
