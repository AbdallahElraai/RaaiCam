import Foundation
import VideoToolbox
import CoreMedia

struct EncodedFrame {
    let annexB: Data
    let isKeyframe: Bool
    let pts: CMTime
}

/// Hardware H.264 High encoder (FR-STR-3): RealTime, no frame reordering,
/// keyframe every 60 frames and on request, average bitrate + 1.5x data-rate cap.
final class H264Encoder {
    /// `sessionLock` makes encode()/set()/stop() mutually exclusive: the session is never released mid-encode.
    private let sessionLock = NSLock()
    private var session: VTCompressionSession?
    private let onFrame: (EncodedFrame) -> Void
    private let onLog: (String) -> Void
    private(set) var width: Int
    private(set) var height: Int

    init(width: Int, height: Int, fps: Int, bitrateKbps: Int,
         onFrame: @escaping (EncodedFrame) -> Void, onLog: @escaping (String) -> Void) throws {
        self.width = width
        self.height = height
        self.onFrame = onFrame
        self.onLog = onLog
        guard (16...8192).contains(width), (16...8192).contains(height), (1...240).contains(fps) else {
            throw EncoderError.badParameters
        }

        var s: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: nil, width: Int32(width), height: Int32(height),
            codecType: kCMVideoCodecType_H264, encoderSpecification: nil,
            imageBufferAttributes: nil, compressedDataAllocator: nil,
            outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        guard status == noErr, let session = s else { throw EncoderError.create(status) }
        self.session = session

        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: 60))
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        applyBitrate(kbps: bitrateKbps)
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    deinit { stop() }

    private func set(_ key: CFString, _ value: CFTypeRef) {
        sessionLock.lock(); defer { sessionLock.unlock() }
        guard let session = session else { return }
        let st = VTSessionSetProperty(session, key: key, value: value)
        if st != noErr { onLog("W encoder property \(key) failed: \(st)") }
    }

    private func applyBitrate(kbps: Int) {
        let bytesPerSecond = Double(kbps) * 1000.0 / 8.0
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: Int64(kbps) * 1000))
        let limits: [NSNumber] = [NSNumber(value: bytesPerSecond * 1.5), NSNumber(value: 1.0)]
        set(kVTCompressionPropertyKey_DataRateLimits, limits as CFArray)
    }

    func setBitrate(kbps: Int) { applyBitrate(kbps: kbps) }

    private let forceLock = NSLock()
    private var forceNext = false

    func forceKeyframe() {
        forceLock.lock(); forceNext = true; forceLock.unlock()
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime, duration: CMTime) {
        sessionLock.lock(); defer { sessionLock.unlock() }
        guard let session = session else { return }
        forceLock.lock(); let force = forceNext; forceNext = false; forceLock.unlock()
        let props: CFDictionary? = force
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame as String: kCFBooleanTrue as Any] as CFDictionary
            : nil
        let st = VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixelBuffer, presentationTimeStamp: pts, duration: duration,
            frameProperties: props, infoFlagsOut: nil
        ) { [weak self] status, _, sampleBuffer in
            guard let self = self else { return }
            guard status == noErr, let sb = sampleBuffer else {
                self.onLog("W encode callback status \(status)")
                return
            }
            self.handle(sb)
        }
        if st != noErr { onLog("W encode submit failed: \(st)") }
    }

    func stop() {
        sessionLock.lock()
        let s = session
        session = nil
        sessionLock.unlock()
        guard let s = s else { return }
        VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(s)
    }

    private func handle(_ sb: CMSampleBuffer) {
        guard CMSampleBufferDataIsReady(sb),
              let block = CMSampleBufferGetDataBuffer(sb),
              let fmt = CMSampleBufferGetFormatDescription(sb) else { return }

        // Keyframe iff kCMSampleAttachmentKey_NotSync is absent (or false).
        var keyframe = true
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let first = arr.first, let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool, notSync {
            keyframe = false
        }

        var parameterSets = [Data]()
        var nalLen: Int32 = 4
        if keyframe {
            var count = 0
            var ptr: UnsafePointer<UInt8>?
            var size = 0
            let st = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                fmt, parameterSetIndex: 0, parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLen)
            guard st == noErr else { onLog("W no parameter sets: \(st)"); return }
            for i in 0..<count {
                var p: UnsafePointer<UInt8>?
                var sz = 0
                if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    fmt, parameterSetIndex: i, parameterSetPointerOut: &p, parameterSetSizeOut: &sz,
                    parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr, let p = p {
                    parameterSets.append(Data(bytes: p, count: sz))
                }
            }
        }

        let total = CMBlockBufferGetDataLength(block)
        var avcc = Data(count: total)
        let copied = avcc.withUnsafeMutableBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return -1 }
            return CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: total, destination: base)
        }
        guard copied == noErr else { onLog("W block copy failed: \(copied)"); return }

        guard let au = AnnexB.accessUnit(avcc: avcc, parameterSets: parameterSets,
                                         isKeyframe: keyframe, nalLengthSize: Int(nalLen)) else {
            onLog("W malformed AVCC sample dropped")
            return
        }
        onFrame(EncodedFrame(annexB: au, isKeyframe: keyframe, pts: CMSampleBufferGetPresentationTimeStamp(sb)))
    }
}

enum EncoderError: Error {
    case create(OSStatus)
    case badParameters
}
