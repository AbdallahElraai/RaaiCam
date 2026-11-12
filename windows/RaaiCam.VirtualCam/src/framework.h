// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam (ported from the Phase 0 spike).
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers
#endif

#include <cassert>
#include <cstdlib>

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
#include <d2d1_1.h>
#include <uuids.h>
#include <sddl.h>

// std
#include <string>
#include <format>

// WIL, fetched by CMake FetchContent (header-only)
#include <wil/result.h>
#include <wil/stl.h>
#include <wil/win32_helpers.h>
#include <wil/com.h>

// C++/WinRT, from the Windows SDK cppwinrt include dir
#include <winrt/base.h>

// project globals
#include "WinTrace.h"
#include "Log.h"

// RaaiCam production CLSID and names
#include "Ids.h"

// Module handle stored by DllMain (DLL_PROCESS_ATTACH); used to pin the DLL for threadpool callbacks.
extern HMODULE g_module;
