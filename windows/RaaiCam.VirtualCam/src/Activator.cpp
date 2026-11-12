// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam (ported from the Phase 0 spike).
#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "Activator.h"

HRESULT Activator::Initialize()
{
	_source = winrt::make_self<MediaSource>();
	RETURN_IF_FAILED(SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
	RETURN_IF_FAILED(SetGUID(MFT_TRANSFORM_CLSID_Attribute, raaicam::CLSID_RaaiCamVCam));
	RETURN_IF_FAILED(_source->Initialize(this));
	return S_OK;
}

// IMFActivate
STDMETHODIMP Activator::ActivateObject(REFIID riid, void** ppv)
{
	WINTRACE(L"Activator::ActivateObject '%s'", GUID_ToStringW(riid).c_str());
	RETURN_HR_IF_NULL(E_POINTER, ppv);
	*ppv = nullptr;
	RETURN_HR_IF(MF_E_SHUTDOWN, !_source);

	// RaaiCam: diagnostics only; std::wstring/std::format may throw, never let it cross the COM boundary.
	try
	{
		// use undoc'd frame server property
		UINT32 pid = 0;
		std::wstring name;
		if (SUCCEEDED(GetUINT32(MF_FRAMESERVER_CLIENTCONTEXT_CLIENTPID, &pid)) && pid)
			name = GetProcessName(pid);
		VLog(L"Activator::ActivateObject riid=%s client=%s", GUID_ToStringW(riid).c_str(), name.empty() ? L"?" : name.c_str());
	}
	catch (...)
	{
	}
	return _source->QueryInterface(riid, ppv);
}

STDMETHODIMP Activator::ShutdownObject()
{
	WINTRACE(L"Activator::ShutdownObject");
	if (_source)
	{
		const HRESULT hr = _source->Shutdown();
		if (hr != MF_E_SHUTDOWN) // already shut down is fine
			LOG_IF_FAILED(hr);
	}
	return S_OK;
}

STDMETHODIMP Activator::DetachObject()
{
	WINTRACE(L"Activator::DetachObject");
	_source = nullptr;
	return S_OK;
}
