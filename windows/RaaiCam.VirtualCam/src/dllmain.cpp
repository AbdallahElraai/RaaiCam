// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam (ported from the Phase 0 spike): DllMain does no work under the loader lock.
#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "SharedRegion.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "Activator.h"

HMODULE g_module = nullptr;

// RaaiCam: only DisableThreadLibraryCalls + the module handle. No logging, ETW registration, file,
// registry or user-name calls here (loader lock). Logging opens its file lazily on first use.
BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved)
{
	UNREFERENCED_PARAMETER(lpReserved);
	if (dwReason == DLL_PROCESS_ATTACH)
	{
		g_module = hModule;
		DisableThreadLibraryCalls(hModule);
	}
	return TRUE;
}

namespace
{
	// Maps any exception (WIL, C++/WinRT, std) to an HRESULT so none crosses the COM boundary.
	HRESULT CurrentExceptionToHResult() noexcept
	{
		try
		{
			throw;
		}
		catch (const wil::ResultException& e)
		{
			return e.GetErrorCode();
		}
		catch (...)
		{
			return winrt::to_hresult();
		}
	}
}

struct ClassFactory : winrt::implements<ClassFactory, IClassFactory>
{
	STDMETHODIMP CreateInstance(IUnknown* outer, GUID const& riid, void** result) noexcept final
	{
		RETURN_HR_IF_NULL(E_POINTER, result);
		*result = nullptr;
		if (outer)
			RETURN_HR(CLASS_E_NOAGGREGATION);

		try
		{
			auto vcam = winrt::make_self<Activator>();
			RETURN_IF_FAILED(vcam->Initialize());
			auto hr = vcam->QueryInterface(riid, result);
			if (FAILED(hr))
				VLog(L"ClassFactory::CreateInstance QueryInterface failed on IID %s hr=0x%08X", GUID_ToStringW(riid).c_str(), hr);
			else
				VLog(L"ClassFactory::CreateInstance activator created");
			return hr;
		}
		catch (...)
		{
			const HRESULT hr = CurrentExceptionToHResult();
			VLog(L"ClassFactory::CreateInstance failed hr=0x%08X", hr);
			return hr;
		}
	}

	STDMETHODIMP LockServer(BOOL lock) noexcept final
	{
		if (lock)
			++winrt::get_module_lock();
		else
			--winrt::get_module_lock();
		return S_OK;
	}
};

__control_entrypoint(DllExport)
STDAPI DllCanUnloadNow()
{
	if (winrt::get_module_lock())
		return S_FALSE;

	// No COM object (so no stream, no DeliveryClock) is alive: release the mapping and the log file
	// here, not at DLL_PROCESS_DETACH (loader lock). Both are re-acquired lazily if we are reused.
	raaicam::SharedRegion::Instance().Release();
	winrt::clear_factory_cache();
	VLogClose();
	return S_OK;
}

_Check_return_
STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID FAR* ppv)
{
	WINTRACE(L"DllGetClassObject rclsid:%s riid:%s", GUID_ToStringW(rclsid).c_str(), GUID_ToStringW(riid).c_str());
	RETURN_HR_IF_NULL(E_POINTER, ppv);
	*ppv = nullptr;

	if (rclsid != raaicam::CLSID_RaaiCamVCam)
		return CLASS_E_CLASSNOTAVAILABLE;

	try
	{
		return winrt::make_self<ClassFactory>()->QueryInterface(riid, ppv);
	}
	catch (...)
	{
		return CurrentExceptionToHResult();
	}
}

using registry_key = winrt::handle_type<registry_traits>;

// As upstream: registers in HKEY_LOCAL_MACHINE (needs elevation), because the Frame Server service
// (LocalService) does not see the interactive user's HKCU. Run only by the installer (Task 4).
STDAPI DllRegisterServer()
{
	try
	{
		std::wstring exePath = wil::GetModuleFileNameW(g_module).get();
		auto clsid = GUID_ToStringW(raaicam::CLSID_RaaiCamVCam, false);
		std::wstring path = L"Software\\Classes\\CLSID\\" + clsid + L"\\InprocServer32";

		// note: a vcam *must* be registered in HKEY_LOCAL_MACHINE
		// for the frame server to be able to talk with it.
		registry_key key;
		RETURN_IF_WIN32_ERROR(RegWriteKey(HKEY_LOCAL_MACHINE, path.c_str(), key.put()));
		RETURN_IF_WIN32_ERROR(RegWriteValue(key.get(), nullptr, exePath));
		RETURN_IF_WIN32_ERROR(RegWriteValue(key.get(), L"ThreadingModel", L"Both"));
		return S_OK;
	}
	catch (...)
	{
		return CurrentExceptionToHResult();
	}
}

STDAPI DllUnregisterServer()
{
	try
	{
		auto clsid = GUID_ToStringW(raaicam::CLSID_RaaiCamVCam, false);
		std::wstring path = L"Software\\Classes\\CLSID\\" + clsid;
		RETURN_IF_WIN32_ERROR(RegDeleteTree(HKEY_LOCAL_MACHINE, path.c_str()));
		return S_OK;
	}
	catch (...)
	{
		return CurrentExceptionToHResult();
	}
}
