using System.Globalization;
using System.Text.RegularExpressions;

namespace RaaiCam.Core.Hosting;

/// <summary>
/// The once-per-second host status line, as stable <c>key=value</c> tokens that scripts parse:
/// <c>link=usb|wifi|none standby=wifi|usb|none kbps=N phone=NAME consumer=WxH|inactive in=F out=F lat=P50/P95ms rtt=Nms rate=N.NMbps thermal=T batt=N%[+]</c>.
/// Values that are unknown print as <c>-</c>. <c>phone=</c> may contain spaces (it ends at <c> consumer=</c>).
/// </summary>
public static partial class StatusLine
{
    public static string Format(HostStatus s)
    {
        var ic = CultureInfo.InvariantCulture;
        string consumer = s.ConsumerWidth > 0 ? $"{s.ConsumerWidth}x{s.ConsumerHeight}" : "inactive";
        string lat = s.LatencyP50Ms is { } p50 && s.LatencyP95Ms is { } p95
            ? string.Create(ic, $"{p50:F0}/{p95:F0}ms") : "-/-ms";
        string rtt = s.RttMs is { } r ? string.Create(ic, $"{r:F0}ms") : "-";
        string rate = s.BitrateMbps is { } b ? string.Create(ic, $"{b:F1}Mbps") : "-";
        string batt = s.BatteryPercent is { } p ? $"{p}%{(s.Charging == true ? "+" : "")}" : "-";
        return string.Create(ic,
            $"link={s.Link} standby={s.Standby} kbps={s.TargetBitrateKbps} phone={s.Phone ?? "-"} consumer={consumer} in={s.FpsIn:F1} out={s.FpsOut:F1} lat={lat} rtt={rtt} rate={rate} thermal={s.Thermal ?? "-"} batt={batt}");
    }

    public sealed record Parsed(string Link, string Standby, int Kbps, string Phone, string Consumer, double In, double Out,
        double? P50Ms, double? P95Ms);

    /// <summary>Parses a line produced by <see cref="Format"/>; null when it is not a status line.</summary>
    public static Parsed? TryParse(string line)
    {
        var m = LineRegex().Match(line);
        if (!m.Success) return null;
        var ic = CultureInfo.InvariantCulture;
        double? p50 = null, p95 = null;
        var lat = LatRegex().Match(m.Groups["lat"].Value);
        if (lat.Success)
        {
            p50 = double.Parse(lat.Groups[1].Value, ic);
            p95 = double.Parse(lat.Groups[2].Value, ic);
        }
        return new Parsed(m.Groups["link"].Value, m.Groups["standby"].Value, int.Parse(m.Groups["kbps"].Value, ic),
            m.Groups["phone"].Value, m.Groups["consumer"].Value, double.Parse(m.Groups["in"].Value, ic),
            double.Parse(m.Groups["out"].Value, ic), p50, p95);
    }

    [GeneratedRegex(@"^link=(?<link>\S+) standby=(?<standby>\S+) kbps=(?<kbps>\d+) phone=(?<phone>.*?) consumer=(?<consumer>\S+) in=(?<in>[\d.]+) out=(?<out>[\d.]+) lat=(?<lat>\S+) ")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^([\d.]+)/([\d.]+)ms$")]
    private static partial Regex LatRegex();
}
