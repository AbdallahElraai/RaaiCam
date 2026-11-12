// RaaiCam virtual camera identifiers. Shared by the media source DLL and the installer tooling (vcamctl).
// Self-contained on purpose: include it without the DLL's precompiled framework headers.
#pragma once
#include <guiddef.h>

namespace raaicam {

// Production media source CLSID {1A9ACDD9-18B6-4E44-9286-761E43846B2C}.
// Generated once for Phase 1A; must never equal the Phase 0 spike's {7EC2CD0F-93C4-4E06-BF91-63F3B541C1E4}.
inline constexpr GUID CLSID_RaaiCamVCam = {0x1a9acdd9, 0x18b6, 0x4e44, {0x92, 0x86, 0x76, 0x1e, 0x43, 0x84, 0x6b, 0x2c}};

// Camera friendly name passed to MFCreateVirtualCamera.
inline constexpr wchar_t kFriendlyName[] = L"RaaiCam";

}  // namespace raaicam

