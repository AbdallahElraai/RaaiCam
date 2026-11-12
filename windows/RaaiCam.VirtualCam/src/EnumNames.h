// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam (ported from the Phase 0 spike).
#pragma once

const std::string WM_ToString(UINT msg);
const std::wstring VARTYPE_ToString(VARTYPE value);
const std::wstring MF_ATTRIBUTE_TYPE_ToString(MF_ATTRIBUTE_TYPE value);
const std::wstring KSPROPERTY_TYPE_ToString(ULONG value);
const std::wstring KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY_ToString(ULONG value);
const std::wstring PROPSETID_VIDCAP_CAMERACONTROL_ToString(ULONG value);
const std::wstring PROPSETID_VIDCAP_VIDEOPROCAMP_ToString(ULONG value);
const std::wstring PROPSETID_VIDCAP_CAMERACONTROL_IMAGE_PIN_CAPABILITY_ToString(ULONG value);
const std::wstring KSPROPERTY_CAMERACONTROL_PERFRAMESETTING_PROPERTY_ToString(ULONG value);
const std::wstring KSPROPERTY_CAMERACONTROL_REGION_OF_INTEREST_ToString(ULONG value);
const std::wstring KSPROPERTY_TOPOLOGY_ToString(ULONG value);
const std::wstring KSPROPERTY_PIN_ToString(ULONG value);
const std::wstring KSPROPSETID_Connection_ToString(ULONG value);


