import XCTest
@testable import RaaiCam

final class SealedChannelTests: XCTestCase {
    func testSealedVectors() throws {
        let v = try V2Vectors.load()
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        let stop = Data(#"{"cmd":"stop"}"#.utf8)

        // I->R counter 0: initiator seals exactly the vector, responder opens it
        let tx = SealedChannel(keys: keys, role: .initiator)
        XCTAssertEqual(try tx.seal(innerType: FrameType.cmd.rawValue, payload: stop), v.sealedPayload)
        XCTAssertEqual(FrameCodec.encode(.sealed, v.sealedPayload), v.sealedFrame)
        let rx = SealedChannel(keys: keys, role: .responder)
        let inner = try rx.open(v.sealedPayload)
        XCTAssertEqual(inner.type, FrameType.cmd.rawValue)
        XCTAssertEqual(inner.payload, stop)

        // u64 big-endian counter in nonce and AAD
        let high: UInt64 = 0x0102030405060708
        let txHigh = SealedChannel(keys: keys, role: .initiator, sendCounter: high, recvCounter: 0)
        XCTAssertEqual(try txHigh.seal(innerType: FrameType.cmd.rawValue, payload: stop), v.highI2R)
        let rxHigh = SealedChannel(keys: keys, role: .responder, sendCounter: 0, recvCounter: high)
        XCTAssertEqual(try rxHigh.open(v.highI2R).payload, stop)

        // R->I INFO at the high counter
        let info = Data(#"{"app":"RaaiCam","version":"1.0.0","device":"iPhone15,2","os":"iOS 17.5"}"#.utf8)
        let respHigh = SealedChannel(keys: keys, role: .responder, sendCounter: high, recvCounter: 0)
        XCTAssertEqual(try respHigh.seal(innerType: FrameType.info.rawValue, payload: info), v.highR2I)
        let iniHigh = SealedChannel(keys: keys, role: .initiator, sendCounter: 0, recvCounter: high)
        let got = try iniHigh.open(v.highR2I)
        XCTAssertEqual(got.type, FrameType.info.rawValue)
        XCTAssertEqual(got.payload, info)
    }

    func testCountersIncrementAndDirectionsSeparated() throws {
        let v = try V2Vectors.load()
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        let tx = SealedChannel(keys: keys, role: .initiator)
        let rx = SealedChannel(keys: keys, role: .responder)
        for i in 0..<5 {
            let p = try tx.seal(innerType: FrameType.ping.rawValue, payload: Data([UInt8(i)]))
            XCTAssertEqual(p.readBE64(at: 0), UInt64(i))
            XCTAssertEqual(try rx.open(p).payload, Data([UInt8(i)]))
        }
        // directions are separated: a responder cannot open its own output
        let echo = SealedChannel(keys: keys, role: .responder)
        let own = try echo.seal(innerType: FrameType.log.rawValue, payload: Data("I x".utf8))
        let echoRx = SealedChannel(keys: keys, role: .responder)
        XCTAssertThrowsError(try echoRx.open(own))
    }

    func testInnerAllowListAndFraming() throws {
        let v = try V2Vectors.load()
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        // forbidden inner types (HELLO, AUTH, SEALED, unknown) close the session
        for t: UInt8 in [1, 2, 9, 0x20, 0] {
            let tx = SealedChannel(keys: keys, role: .initiator)
            let rx = SealedChannel(keys: keys, role: .responder)
            let p = try tx.seal(innerType: t, payload: Data([1, 2, 3]))
            XCTAssertThrowsError(try rx.open(p), "type \(t)")
        }
        // every allowed type passes
        for t: UInt8 in [3, 4, 5, 6, 7, 8, 10] {
            let tx = SealedChannel(keys: keys, role: .initiator)
            let rx = SealedChannel(keys: keys, role: .responder)
            XCTAssertEqual(try rx.open(try tx.seal(innerType: t, payload: Data([9]))).type, t)
        }
        // inner length field must match the remaining plaintext
        let tx = SealedChannel(keys: keys, role: .initiator)
        let rx = SealedChannel(keys: keys, role: .responder)
        let bad = try tx.sealRaw(plaintext: Data([3, 0, 0, 0, 9, 1, 2]))
        XCTAssertThrowsError(try rx.open(bad))
        // truncated payloads
        let rx2 = SealedChannel(keys: keys, role: .responder)
        XCTAssertThrowsError(try rx2.open(Data(repeating: 0, count: 10)))
    }

    func testConfirmationFlag() throws {
        let v = try V2Vectors.load()
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        let ini = SealedChannel(keys: keys, role: .initiator)
        let resp = SealedChannel(keys: keys, role: .responder)
        XCTAssertFalse(ini.isConfirmed)
        _ = try ini.open(try resp.seal(innerType: FrameType.info.rawValue, payload: Data("{}".utf8)))
        XCTAssertTrue(ini.isConfirmed)
    }
}
