import Foundation

enum PairingURLError: Error, Equatable {
    case notRaaiCam, badVersion, malformed, expired

    var userMessage: String {
        switch self {
        case .notRaaiCam: return "That is not a RaaiCam code."
        case .badVersion: return "This code is from a different RaaiCam version. Update both apps."
        case .malformed: return "That code could not be read. Show a new one on your PC."
        case .expired: return "That code has expired. Show a new one on your PC."
        }
    }
}

/// Parses `raaicam://pair?v=2&pc=<b64url 16>&name=<urlencoded>&pk=<b64url 32>&tok=<b64url 16>&exp=<unix s>`.
/// The result is never logged.
enum PairingURL {
    static let tokenLifetime: TimeInterval = 300

    static func parse(_ string: String, now: Date = Date()) -> Result<PairingToken, PairingURLError> {
        guard string.count < 2048,
              let comps = URLComponents(string: string.trimmingCharacters(in: .whitespacesAndNewlines)),
              comps.scheme?.lowercased() == "raaicam", comps.host?.lowercased() == "pair" else { return .failure(.notRaaiCam) }

        var fields = [String: String]()
        for item in comps.percentEncodedQueryItems ?? [] {
            guard fields[item.name] == nil else { return .failure(.malformed) }
            fields[item.name] = item.value ?? ""
        }
        guard fields["v"] == "2" else { return .failure(.badVersion) }
        guard let pc = fields["pc"].flatMap({ Data(base64URL: $0) }), pc.count == 16,
              let pk = fields["pk"].flatMap({ Data(base64URL: $0) }), pk.count == 32,
              let tok = fields["tok"].flatMap({ Data(base64URL: $0) }), tok.count == 16,
              let exp = fields["exp"].flatMap({ Int64($0) }) else { return .failure(.malformed) }
        let expiry = Date(timeIntervalSince1970: TimeInterval(exp))
        guard expiry > now else { return .failure(.expired) }

        var name = (fields["name"] ?? "").replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? ""
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: name.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.prefix(64))
        name = String(scalars).trimmingCharacters(in: .whitespaces)
        if name.isEmpty { name = "your PC" }

        // Never trust the PC's clock for more than the token lifetime.
        let effective = min(expiry, now.addingTimeInterval(tokenLifetime))
        return .success(PairingToken(token: tok, pcId: pc, pcPub: pk, name: name, expiresAt: effective))
    }
}
