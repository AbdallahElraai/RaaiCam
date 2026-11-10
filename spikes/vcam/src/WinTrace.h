// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see spikes/THIRD_PARTY_NOTICES.md.
// Modified for the RaaiCam Phase 0 spike.
#pragma once

HRESULT GetTraceId(GUID* pGuid);

ULONG WinTraceRegister();
void WinTraceUnregister();

void WinTrace(UCHAR Level, ULONGLONG Keyword, PCWSTR String);
void WinTraceFormat(UCHAR Level, ULONGLONG Keyword, PCWSTR pszFormat, ...);

void WinTrace(UCHAR Level, ULONGLONG Keyword, PCSTR String);
void WinTraceFormat(UCHAR Level, ULONGLONG Keyword, PCSTR pszFormat, ...);

#ifdef _DEBUG
#define WINTRACE(...) WinTraceFormat(0, 0, __VA_ARGS__)
#else
#define WINTRACE __noop
#endif
#pragma once
