// vcamctl: control CLI for the RaaiCam virtual camera.
//   vcamctl create   register + start the System-lifetime camera (run NON-elevated: Access_CurrentUser)
//   vcamctl remove   remove the camera
//   vcamctl status   one line of JSON
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <cstdio>
#include <string>
#include "Ids.h"

using namespace raaicam;

static std::wstring ClsidString()
{
	wchar_t buf[64];
	StringFromGUID2(CLSID_RaaiCamVCam, buf, 64);
	return buf;
}

static HRESULT MakeCamera(IMFVirtualCamera** cam)
{
	return MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_System,
		MFVirtualCameraAccess_CurrentUser, kFriendlyName, ClsidString().c_str(), nullptr, 0, cam);
}

static bool CameraPresent()
{
	IMFAttributes* attrs = nullptr;
	if (FAILED(MFCreateAttributes(&attrs, 1)))
		return false;
	attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
	IMFActivate** devs = nullptr;
	UINT32 count = 0;
	bool found = false;
	if (SUCCEEDED(MFEnumDeviceSources(attrs, &devs, &count)))
	{
		for (UINT32 i = 0; i < count; i++)
		{
			wchar_t* name = nullptr;
			UINT32 len = 0;
			if (!found && SUCCEEDED(devs[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len)))
			{
				// Windows lists the camera with a suffix; accept exactly these two names.
				found = wcscmp(name, kFriendlyName) == 0 || wcscmp(name, L"RaaiCam (Windows Virtual Camera)") == 0;
				CoTaskMemFree(name);
			}
			devs[i]->Release();
		}
		CoTaskMemFree(devs);
	}
	attrs->Release();
	return found;
}

static int Create()
{
	if (CameraPresent())
	{
		wprintf(L"ALREADY_EXISTS\n");
		return 0;
	}
	IMFVirtualCamera* cam = nullptr;
	HRESULT hr = MakeCamera(&cam);
	if (SUCCEEDED(hr))
	{
		hr = cam->Start(nullptr);
		cam->Release();
	}
	if (FAILED(hr))
	{
		wprintf(L"ERROR 0x%08X\n", (unsigned)hr);
		return 2;
	}
	wprintf(L"CREATED\n");
	return 0;
}

static int Remove()
{
	if (!CameraPresent())
	{
		wprintf(L"NOT_FOUND\n");
		return 0;
	}
	IMFVirtualCamera* cam = nullptr;
	HRESULT hr = MakeCamera(&cam);
	if (SUCCEEDED(hr))
	{
		hr = cam->Remove();
		cam->Release();
	}
	if (FAILED(hr))
	{
		wprintf(L"ERROR 0x%08X\n", (unsigned)hr);
		return 2;
	}
	wprintf(L"REMOVED\n");
	return 0;
}

static std::wstring JsonEscape(const std::wstring& s)
{
	std::wstring o;
	for (wchar_t c : s)
	{
		if (c == L'\\' || c == L'"')
			o += L'\\';
		o += c;
	}
	return o;
}

static bool ReadRegString(HKEY root, const wchar_t* key, const wchar_t* value, REGSAM view, std::wstring& out)
{
	HKEY h = nullptr;
	if (RegOpenKeyExW(root, key, 0, KEY_QUERY_VALUE | view, &h) != ERROR_SUCCESS)
		return false;
	wchar_t buf[2048];
	DWORD cb = sizeof(buf) - sizeof(wchar_t), type = 0;
	LSTATUS r = RegQueryValueExW(h, value, nullptr, &type, (BYTE*)buf, &cb);
	RegCloseKey(h);
	if (r != ERROR_SUCCESS || type != REG_SZ)
		return false;
	buf[cb / sizeof(wchar_t)] = 0;
	out = buf;
	return true;
}

static bool KeyExists(HKEY root, const wchar_t* key, REGSAM view)
{
	HKEY h = nullptr;
	if (RegOpenKeyExW(root, key, 0, KEY_QUERY_VALUE | view, &h) != ERROR_SUCCESS)
		return false;
	RegCloseKey(h);
	return true;
}

// True when no allow-ACE with write/modify/full rights goes to anyone but Administrators, SYSTEM, TrustedInstaller
// (CREATOR OWNER ignored).
static bool FolderAdminOnly(const std::wstring& dir)
{
	PACL dacl = nullptr;
	PSECURITY_DESCRIPTOR sd = nullptr;
	if (GetNamedSecurityInfoW(dir.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, &dacl, nullptr, &sd) != ERROR_SUCCESS)
		return false;
	bool ok = dacl != nullptr;  // a NULL DACL grants everyone full access
	const wchar_t* allowed[] = {L"S-1-5-32-544", L"S-1-5-18", L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", L"S-1-3-0"};
	const DWORD writeMask = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | FILE_DELETE_CHILD |
		DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
	if (ok)
	{
		for (DWORD i = 0; i < dacl->AceCount && ok; i++)
		{
			ACE_HEADER* h = nullptr;
			if (!GetAce(dacl, i, (void**)&h) || h->AceType != ACCESS_ALLOWED_ACE_TYPE)
				continue;
			auto* ace = (ACCESS_ALLOWED_ACE*)h;
			if (!(ace->Mask & writeMask))
				continue;
			wchar_t* str = nullptr;
			bool known = false;
			if (ConvertSidToStringSidW((PSID)&ace->SidStart, &str))
			{
				for (auto a : allowed)
					known = known || _wcsicmp(str, a) == 0;
				LocalFree(str);
			}
			ok = known;
		}
	}
	LocalFree(sd);
	return ok;
}

static int Status()
{
	std::wstring clsidKey = L"SOFTWARE\\Classes\\CLSID\\" + ClsidString() + L"\\InprocServer32";
	bool registered = KeyExists(HKEY_LOCAL_MACHINE, clsidKey.c_str(), KEY_WOW64_64KEY);
	std::wstring dll;
	if (registered)
		ReadRegString(HKEY_LOCAL_MACHINE, clsidKey.c_str(), nullptr, KEY_WOW64_64KEY, dll);
	bool adminOnly = false;
	if (!dll.empty())
	{
		auto slash = dll.find_last_of(L'\\');
		if (slash != std::wstring::npos)
			adminOnly = FolderAdminOnly(dll.substr(0, slash));
	}
	std::wstring sid;
	bool haveSid = ReadRegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\RaaiCam\\VirtualCam", L"AllowedUserSid", KEY_WOW64_64KEY, sid);

	wprintf(L"{\"registered\":%s,\"dllPath\":\"%s\",\"dllPathAdminOnly\":%s,\"cameraPresent\":%s,\"allowedUserSid\":",
		registered ? L"true" : L"false", JsonEscape(dll).c_str(), adminOnly ? L"true" : L"false", CameraPresent() ? L"true" : L"false");
	if (haveSid)
		wprintf(L"\"%s\"}\n", JsonEscape(sid).c_str());
	else
		wprintf(L"null}\n");
	return 0;
}

int wmain(int argc, wchar_t** argv)
{
	if (argc != 2)
	{
		wprintf(L"usage: vcamctl create|remove|status\n");
		return 1;
	}
	std::wstring cmd = argv[1];
	if (cmd != L"create" && cmd != L"remove" && cmd != L"status")
	{
		wprintf(L"usage: vcamctl create|remove|status\n");
		return 1;
	}
	HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	if (SUCCEEDED(hr))
		hr = MFStartup(MF_VERSION);
	if (FAILED(hr))
	{
		wprintf(L"ERROR 0x%08X\n", (unsigned)hr);
		return 2;
	}
	int rc = cmd == L"create" ? Create() : cmd == L"remove" ? Remove() : Status();
	MFShutdown();
	CoUninitialize();
	return rc;
}
