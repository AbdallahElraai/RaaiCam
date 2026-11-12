#include "Security.h"

#include <windows.h>
#include <sddl.h>

namespace raaicam {

namespace {

// Only literal "S-1-<digits>(-<digits>)*" strings: ConvertStringSidToSidW alone also accepts SDDL
// aliases such as "WD" (Everyone) which must never reach the DACL.
bool LooksLikeSidLiteral(const std::wstring& s) {
  if (s.size() < 5 || s.size() > 184 || s.compare(0, 4, L"S-1-") != 0) return false;
  bool lastDash = true;  // position 3 is a dash; a digit must follow
  for (size_t i = 4; i < s.size(); ++i) {
    const wchar_t c = s[i];
    if (c >= L'0' && c <= L'9') {
      lastDash = false;
    } else if (c == L'-' && !lastDash) {
      lastDash = true;
    } else {
      return false;
    }
  }
  return !lastDash;
}

// Only per-user SIDs: local/domain accounts (S-1-5-21-...) and Microsoft accounts / AAD (S-1-12-1-...).
// Group and well-known SIDs (Everyone S-1-1-0, Authenticated Users S-1-5-11, Users S-1-5-32-545, ...) are refused.
bool IsUserSidPrefix(const std::wstring& s) {
  return s.compare(0, 9, L"S-1-5-21-") == 0 || s.compare(0, 9, L"S-1-12-1-") == 0;
}

}  // namespace

std::wstring BuildMappingSddl(const std::wstring& allowedUserSid) {
  if (!LooksLikeSidLiteral(allowedUserSid) || !IsUserSidPrefix(allowedUserSid)) return {};
  PSID sid = nullptr;
  if (!ConvertStringSidToSidW(allowedUserSid.c_str(), &sid)) return {};
  LocalFree(sid);
  return L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;" + allowedUserSid + L")";
}

std::optional<std::wstring> ReadAllowedUserSid() {
  wchar_t buf[256];
  DWORD bytes = sizeof(buf);
  LSTATUS rc = RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\RaaiCam\\VirtualCam", L"AllowedUserSid",
                            RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, buf, &bytes);
  if (rc != ERROR_SUCCESS) return std::nullopt;
  std::wstring s(buf);
  if (s.empty()) return std::nullopt;
  return s;
}

}  // namespace raaicam
