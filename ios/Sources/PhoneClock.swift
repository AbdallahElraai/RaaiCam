import Foundation
import CoreMedia
import Darwin

/// Clock choice (protocol: "phone mach_continuous_time in microseconds"):
/// - `nowUs()` reads CLOCK_MONOTONIC_RAW, which on Darwin is mach_continuous_time in ns / 1000
///   (keeps counting through device sleep). Used for `phoneRecvUs`.
/// - Capture sample PTS values are on the host time clock (CMClockGetHostTimeClock), which is
///   mach_absolute_time based (CLOCK_UPTIME_RAW; stops during sleep). To put `captureUs` on the SAME
///   timeline as `phoneRecvUs`, add the current (continuous - uptime) offset, which is exactly the
///   accumulated sleep time and is constant while awake. Both values then share one clock.
enum PhoneClock {
    static func nowUs() -> UInt64 {
        clock_gettime_nsec_np(CLOCK_MONOTONIC_RAW) / 1000
    }

    static func uptimeUs() -> UInt64 {
        clock_gettime_nsec_np(CLOCK_UPTIME_RAW) / 1000
    }

    /// Host-clock PTS -> continuous-clock microseconds.
    static func captureUs(fromHostPTS pts: CMTime) -> UInt64 {
        let ptsUs = Int64((CMTimeGetSeconds(pts) * 1_000_000).rounded())
        let sleepOffset = Int64(nowUs()) - Int64(uptimeUs())
        return UInt64(max(0, ptsUs + sleepOffset))
    }
}
