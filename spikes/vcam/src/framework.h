// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see spikes/THIRD_PARTY_NOTICES.md.
// Modified for the RaaiCam Phase 0 spike.
#pragma once

#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers

#define _CRTDBG_MAP_ALLOC
#include <cstdlib>
#include <crtdbg.h>
#ifdef _DEBUG
#define DBG_NEW new ( _NORMAL_BLOCK , __FILE__ , __LINE__ )
// replace _NORMAL_BLOCK with _CLIENT_BLOCK if you want the allocations to be of _CLIENT_BLOCK type
#else
#define DBG_NEW new
#endif

// Windows Header Files
#include <windows.h>
#include <evntprov.h>
#include <strsafe.h>
#include <initguid.h>
#include <propvarutil.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <mferror.h>
#include <mfcaptureengine.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <dxgi.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <uuids.h>
#include <sddl.h>

// std
#include <string>
#include <format>

// WIL, fetched by CMake FetchContent (header-only)
#include "wil/result.h"
#include "wil/stl.h"
#include "wil/win32_helpers.h"
#include "wil/com.h"

// C++/WinRT, from the Windows SDK cppwinrt include dir
#include "winrt/base.h"

// project globals
#include "WinTrace.h"
#include "Log.h"


// RaaiCam spike CLSID: 7ec2cd0f-93c4-4e06-bf91-63f3b541c1e4
extern GUID CLSID_VCam;

