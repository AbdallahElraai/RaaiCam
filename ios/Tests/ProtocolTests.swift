import XCTest
@testable import RaaiCam

func hexData(_ s: String) -> Data {
    Data(s.split(separator: " ").map { UInt8($0, radix: 16)! })
}

final class ProtocolTests: XCTestCase {
    struct Vector { let name: String; let type: UInt8; let payload: Data; let frame: Data }

    func loadVectors() throws -> [Vector] {
        let bundle = Bundle(for: ProtocolTests.self)
        let url = try XCTUnwrap(bundle.url(forResource: "test-vectors", withExtension: "json"))
        let obj = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        let frames = obj["frames"] as! [[String: Any]]
        return frames.map { f in
            let payload: Data
            if let s = f["payloadUtf8"] as? String { payload = Data(s.utf8) }
            else { payload = hexData(f["payloadHex"] as! String) }
            return Vector(name: f["name"] as! String, type: UInt8(f["type"] as! Int),
                          payload: payload, frame: hexData(f["hex"] as! String))
        }
    }

    func testVectorsRoundTrip() throws {
        let vectors = try loadVectors()
        XCTAssertGreaterThanOrEqual(vectors.count, 9)
        for v in vectors {
            XCTAssertEqual(FrameCodec.encode(type: v.type, payload: v.payload), v.frame, v.name)
            var dec = FrameDecoder()
            let frames = try dec.feed(v.frame)
            XCTAssertEqual(frames.count, 1, v.name)
            XCTAssertEqual(frames.first?.type, v.type, v.name)
            XCTAssertEqual(frames.first?.payload, v.payload, v.name)
        }
    }

    func testDecoderRejectsOversize() {
        var dec = FrameDecoder()
        // length = 4 MiB + 1
        let header = Data([0x06, 0x00, 0x40, 0x00, 0x01])
        XCTAssertThrowsError(try dec.feed(header)) { err in
            XCTAssertEqual(err as? ProtocolError, .oversize(4 * 1024 * 1024 + 1))
        }
        // exactly 4 MiB announced is legal (just incomplete)
        var dec2 = FrameDecoder()
        XCTAssertEqual(try dec2.feed(Data([0x06, 0x00, 0x40, 0x00, 0x00])).count, 0)
    }

    func testPartialFramesReassemble() throws {
        let vectors = try loadVectors()
        let v = try XCTUnwrap(vectors.first { $0.name == "video-keyframe" })
        var dec = FrameDecoder()
        var got = [Frame]()
        for b in v.frame { got += try dec.feed(Data([b])) }
        XCTAssertEqual(got.count, 1)
        XCTAssertEqual(got[0].payload, v.payload)
        // two frames back to back in one chunk
        var dec2 = FrameDecoder()
        let two = try dec2.feed(v.frame + v.frame)
        XCTAssertEqual(two.count, 2)
    }

    func testAnnexBFromAvcc() {
        let avcc = Data([0, 0, 0, 5, 1, 2, 3, 4, 5])
        XCTAssertEqual(AnnexB.convert(avcc: avcc, nalLengthSize: 4), Data([0, 0, 0, 1, 1, 2, 3, 4, 5]))
        let two = Data([0, 0, 0, 2, 9, 8, 0, 0, 0, 1, 7])
        XCTAssertEqual(AnnexB.convert(avcc: two, nalLengthSize: 4), Data([0, 0, 0, 1, 9, 8, 0, 0, 0, 1, 7]))
        // malformed: length beyond data
        XCTAssertNil(AnnexB.convert(avcc: Data([0, 0, 0, 9, 1]), nalLengthSize: 4))
    }

    func testAnnexBKeyframePrependsParameterSets() {
        let sps = Data([0x67, 1, 2]), pps = Data([0x68, 3])
        let avcc = Data([0, 0, 0, 2, 0x65, 9])
        let key = AnnexB.accessUnit(avcc: avcc, parameterSets: [sps, pps], isKeyframe: true, nalLengthSize: 4)
        XCTAssertEqual(key, Data([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 9]))
        let delta = AnnexB.accessUnit(avcc: avcc, parameterSets: [sps, pps], isKeyframe: false, nalLengthSize: 4)
        XCTAssertEqual(delta, Data([0, 0, 0, 1, 0x65, 9]))
    }

    func testStartCommandParses() throws {
        let json = #"{"cmd":"start","width":1920,"height":1080,"fps":30,"lens":"wide","bitrateKbps":8000}"#
        let cmd = try Command.parse(Data(json.utf8))
        XCTAssertEqual(cmd, .start(StartCommand(width: 1920, height: 1080, fps: 30, lens: .wide, bitrateKbps: 8000)))
    }

    func testOtherCommandsParse() throws {
        XCTAssertEqual(try Command.parse(Data(#"{"cmd":"stop"}"#.utf8)), .stop)
        XCTAssertEqual(try Command.parse(Data(#"{"cmd":"keyframe"}"#.utf8)), .keyframe)
        XCTAssertEqual(try Command.parse(Data(#"{"cmd":"bitrate","kbps":6000}"#.utf8)), .bitrate(kbps: 6000))
        XCTAssertEqual(try Command.parse(Data(#"{"cmd":"lens","lens":"ultra"}"#.utf8)), .lens(.ultra))
        XCTAssertEqual(try Command.parse(Data(#"{"cmd":"dance"}"#.utf8)), .unknown("dance"))
        XCTAssertThrowsError(try Command.parse(Data("not json".utf8)))
    }

    func testTelemetryEncodesThermalState() throws {
        XCTAssertEqual(TelemetrySnapshot.thermalString(.serious), "serious")
        XCTAssertEqual(TelemetrySnapshot.thermalString(.nominal), "nominal")
        XCTAssertEqual(TelemetrySnapshot.thermalString(.fair), "fair")
        XCTAssertEqual(TelemetrySnapshot.thermalString(.critical), "critical")
        var t = TelemetrySnapshot()
        t.thermal = TelemetrySnapshot.thermalString(.serious)
        t.width = 1920; t.height = 1080; t.streaming = true; t.lens = .ultra
        let obj = try JSONSerialization.jsonObject(with: t.jsonData()) as! [String: Any]
        XCTAssertEqual(obj["thermal"] as? String, "serious")
        XCTAssertEqual(obj["width"] as? Int, 1920)
        XCTAssertEqual(obj["streaming"] as? Bool, true)
        XCTAssertEqual(obj["lens"] as? String, "ultra")
        for key in ["battery", "charging", "height", "fpsCapture", "fpsEncode", "bitrateKbps", "paused"] {
            XCTAssertNotNil(obj[key], key)
        }
    }

    func testPingPongPayloads() throws {
        let vectors = try loadVectors()
        let ping = try XCTUnwrap(vectors.first { $0.name == "ping" })
        let pong = try XCTUnwrap(vectors.first { $0.name == "pong" })
        let sent = try XCTUnwrap(Payloads.parsePing(ping.payload))
        XCTAssertEqual(sent, 1_000_000)
        XCTAssertEqual(Payloads.pong(pcSendUs: sent, phoneRecvUs: 2_000_000), pong.payload)
    }

    func testVideoPayloadLayout() throws {
        let vectors = try loadVectors()
        let v = try XCTUnwrap(vectors.first { $0.name == "video-keyframe" })
        let au = v.payload.suffix(from: v.payload.startIndex + 13)
        let built = Payloads.video(captureUs: 0x0102030405060708, seq: 42, keyframe: true, accessUnit: Data(au))
        XCTAssertEqual(built, v.payload)
    }

    func testHelloRoundTrip() throws {
        let data = Hello.phone(version: "1.0.0", device: "iPhone15,2", os: "iOS 17.5")
        let parsed = try Hello.parse(data)
        XCTAssertEqual(parsed.proto, 1)
        XCTAssertEqual(parsed.role, "phone")
        XCTAssertThrowsError(try Hello.parse(Data("{}".utf8)))
    }

    func testLoopbackPeerCheck() {
        XCTAssertTrue(PeerAddress.isLoopback(v4: 0x7F000001))
        XCTAssertFalse(PeerAddress.isLoopback(v4: 0xC0A80001))
        var lo6 = [UInt8](repeating: 0, count: 16); lo6[15] = 1
        XCTAssertTrue(PeerAddress.isLoopback(v6: lo6))
        var mapped = [UInt8](repeating: 0, count: 16)
        mapped[10] = 0xff; mapped[11] = 0xff; mapped[12] = 127; mapped[15] = 1
        XCTAssertTrue(PeerAddress.isLoopback(v6: mapped))
        var other = [UInt8](repeating: 0, count: 16); other[0] = 0xfe; other[1] = 0x80; other[15] = 1
        XCTAssertFalse(PeerAddress.isLoopback(v6: other))
        mapped[12] = 192
        XCTAssertFalse(PeerAddress.isLoopback(v6: mapped))
    }
}
