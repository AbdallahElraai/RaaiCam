namespace RaaiCam.Core.Phone;

/// <summary>
/// Estimates the offset between the phone clock and the PC clock from PING/PONG samples.
/// Thread-safe. Offset = phone - pc, taken from the minimum-RTT sample among those whose
/// pcRecvUs is within the last 10 s of the newest sample.
/// </summary>
public sealed class ClockSync
{
    const ulong WindowUs = 10_000_000;

    readonly object _gate = new();
    readonly List<Sample> _samples = new();

    readonly record struct Sample(ulong PcRecvUs, long RttUs, long OffsetUs);

    public void AddSample(ulong pcSendUs, ulong phoneRecvUs, ulong pcRecvUs)
    {
        if (pcRecvUs < pcSendUs) return; // negative RTT: ignore
        long rtt = (long)(pcRecvUs - pcSendUs);
        long offset = (long)phoneRecvUs - ((long)pcSendUs + rtt / 2);
        lock (_gate)
        {
            _samples.Add(new Sample(pcRecvUs, rtt, offset));
            ulong newest = 0;
            foreach (var s in _samples) newest = Math.Max(newest, s.PcRecvUs);
            ulong cutoff = newest > WindowUs ? newest - WindowUs : 0;
            _samples.RemoveAll(s => s.PcRecvUs < cutoff);
        }
    }

    Sample? Best()
    {
        lock (_gate)
        {
            Sample? best = null;
            foreach (var s in _samples)
                if (best is null || s.RttUs < best.Value.RttUs) best = s;
            return best;
        }
    }

    /// <summary>phone clock minus PC clock, in microseconds; null until a sample exists.</summary>
    public long? OffsetUs => Best()?.OffsetUs;

    /// <summary>RTT of the selected sample, in milliseconds.</summary>
    public double? RttMs => Best() is { } b ? b.RttUs / 1000.0 : null;

    public long? ToPcUs(ulong phoneUs) => OffsetUs is { } o ? (long)phoneUs - o : null;
}
