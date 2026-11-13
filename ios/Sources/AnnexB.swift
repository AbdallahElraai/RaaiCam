import Foundation

/// AVCC (length-prefixed) to Annex-B (start-code) conversion. Pure Swift.
enum AnnexB {
    static let startCode: [UInt8] = [0, 0, 0, 1]

    /// Returns nil if the buffer is malformed (NAL length overruns the data).
    static func convert(avcc: Data, nalLengthSize: Int = 4) -> Data? {
        let bytes = [UInt8](avcc)
        guard (1...4).contains(nalLengthSize) else { return nil }
        var out = Data(capacity: bytes.count + 16)
        var pos = 0
        while pos < bytes.count {
            guard pos + nalLengthSize <= bytes.count else { return nil }
            var len = 0
            for i in 0..<nalLengthSize { len = len << 8 | Int(bytes[pos + i]) }
            pos += nalLengthSize
            guard len >= 0, pos + len <= bytes.count else { return nil }
            out.append(contentsOf: startCode)
            out.append(contentsOf: bytes[pos..<(pos + len)])
            pos += len
        }
        return out
    }

    /// Full Annex-B access unit; keyframes get SPS/PPS prepended.
    static func accessUnit(avcc: Data, parameterSets: [Data], isKeyframe: Bool, nalLengthSize: Int = 4) -> Data? {
        guard let body = convert(avcc: avcc, nalLengthSize: nalLengthSize) else { return nil }
        guard isKeyframe else { return body }
        var out = Data()
        for ps in parameterSets {
            out.append(contentsOf: startCode)
            out.append(ps)
        }
        out.append(body)
        return out
    }
}
