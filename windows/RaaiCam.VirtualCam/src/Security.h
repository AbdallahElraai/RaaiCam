#pragma once
#include <optional>
#include <string>

namespace raaicam {

// D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;<sid>). Empty unless allowedUserSid is a literal user SID with prefix
// S-1-5-21- (local/domain account) or S-1-12-1- (Microsoft/AAD account); groups and well-known SIDs are refused.
std::wstring BuildMappingSddl(const std::wstring& allowedUserSid);

// HKLM\SOFTWARE\RaaiCam\VirtualCam\AllowedUserSid (REG_SZ, 64-bit view).
std::optional<std::wstring> ReadAllowedUserSid();

}  // namespace raaicam
