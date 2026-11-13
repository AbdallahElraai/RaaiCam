using System.Net;
using System.Text;

namespace RaaiCam.Core.Discovery;

public enum MdnsRecordType : ushort { A = 1, Ptr = 12, Txt = 16, Srv = 33, Any = 255 }

public sealed record MdnsQuestion(string Name, MdnsRecordType Type);

/// <summary>One parsed answer record. Only the fields relevant to <see cref="Type"/> are set.</summary>
public sealed record MdnsRecord(
    string Name,
    MdnsRecordType Type,
    string? Target = null,
    int Port = 0,
    IPAddress? Address = null,
    IReadOnlyDictionary<string, string>? Txt = null,
    uint Ttl = 0);

/// <summary>
/// Minimal DNS wire-format reader/writer for mDNS. The parser treats every packet as hostile: all reads are bounds
/// checked, name compression pointers must point strictly backwards and are limited to 16 jumps, names are capped at
/// 255 bytes (wire form), at most 50 records are processed, and malformed input yields fewer records, never an exception.
/// </summary>
public static class MdnsPacket
{
    public const int MaxRecords = 50;
    public const int MaxNameBytes = 255;
    const int MaxJumps = 16;
    const int MaxTxtEntries = 16;

    /// <summary>Builds a query with the QU bit set on every question. Our queries come from an ephemeral port (not 5353), so responders treat them as legacy unicast queries and answer to that port regardless; the bit is a harmless hint.</summary>
    public static byte[] BuildQuery(IEnumerable<MdnsQuestion> questions)
    {
        var qs = questions.ToList();
        var b = new List<byte> { 0, 0, 0, 0, (byte)(qs.Count >> 8), (byte)qs.Count, 0, 0, 0, 0, 0, 0 };
        foreach (var q in qs)
        {
            foreach (var label in q.Name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = Encoding.UTF8.GetBytes(label);
                if (l.Length > 63) throw new ArgumentException("label too long", nameof(questions));
                b.Add((byte)l.Length);
                b.AddRange(l);
            }
            b.Add(0);
            b.Add((byte)((ushort)q.Type >> 8)); b.Add((byte)q.Type);
            b.Add(0x80); b.Add(0x01); // QU | IN
        }
        return b.ToArray();
    }

    /// <summary>Parses the answer/authority/additional records of an mDNS response. Queries and garbage give an empty list.</summary>
    public static IReadOnlyList<MdnsRecord> Parse(ReadOnlySpan<byte> p)
    {
        var result = new List<MdnsRecord>();
        try
        {
            if (p.Length < 12) return result;
            int flags = U16(p, 2);
            if ((flags & 0x8000) == 0) return result; // a query, not a response
            int qd = U16(p, 4);
            long total = (long)U16(p, 6) + U16(p, 8) + U16(p, 10);
            int pos = 12;

            for (int i = 0; i < qd; i++)
            {
                if (!ReadName(p, ref pos, out _)) return result;
                if (pos + 4 > p.Length) return result;
                pos += 4;
            }

            for (long i = 0; i < total && i < MaxRecords; i++)
            {
                if (!ReadName(p, ref pos, out var name)) break;
                if (pos + 10 > p.Length) break;
                var type = (MdnsRecordType)U16(p, pos);
                int cls = U16(p, pos + 2) & 0x7FFF;
                int rdlen = U16(p, pos + 8);
                uint ttl = ((uint)U16(p, pos + 4) << 16) | (uint)U16(p, pos + 6);
                int start = pos + 10;
                if (start + rdlen > p.Length) break;
                pos = start + rdlen;
                if (cls != 1) continue;

                var rd = p.Slice(start, rdlen);
                switch (type)
                {
                    case MdnsRecordType.A:
                        if (rdlen == 4) result.Add(new MdnsRecord(name, type, Address: new IPAddress(rd), Ttl: ttl));
                        break;
                    case MdnsRecordType.Ptr:
                    {
                        int q = start;
                        if (ReadName(p, ref q, out var target) && q <= start + rdlen)
                            result.Add(new MdnsRecord(name, type, Target: target, Ttl: ttl));
                        break;
                    }
                    case MdnsRecordType.Srv:
                    {
                        if (rdlen < 7) break;
                        int port = U16(p, start + 4);
                        int q = start + 6;
                        if (ReadName(p, ref q, out var target) && q <= start + rdlen)
                            result.Add(new MdnsRecord(name, type, Target: target, Port: port, Ttl: ttl));
                        break;
                    }
                    case MdnsRecordType.Txt:
                        result.Add(new MdnsRecord(name, type, Txt: ParseTxt(rd), Ttl: ttl));
                        break;
                }
            }
        }
        catch (Exception) { /* defence in depth: hostile input must never escape the browse loop */ }
        return result;
    }

    static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> rd)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < rd.Length && d.Count < MaxTxtEntries)
        {
            int n = rd[i++];
            if (i + n > rd.Length) break;
            var s = Encoding.UTF8.GetString(rd.Slice(i, n));
            i += n;
            int eq = s.IndexOf('=');
            var key = eq < 0 ? s : s[..eq];
            if (key.Length == 0) continue;
            d.TryAdd(key, eq < 0 ? "" : s[(eq + 1)..]); // first occurrence wins (RFC 6763)
        }
        return d;
    }

    static int U16(ReadOnlySpan<byte> p, int at) => (p[at] << 8) | p[at + 1];

    /// <summary>Reads a possibly compressed name. <paramref name="pos"/> advances past the name's in-place bytes.</summary>
    static bool ReadName(ReadOnlySpan<byte> p, ref int pos, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        int cur = pos;
        bool jumped = false;
        int jumps = 0, wire = 0;
        while (true)
        {
            if (cur >= p.Length) return false;
            int b = p[cur];
            if (b == 0)
            {
                if (!jumped) pos = cur + 1;
                break;
            }
            if ((b & 0xC0) == 0xC0)
            {
                if (cur + 1 >= p.Length) return false;
                int ptr = ((b & 0x3F) << 8) | p[cur + 1];
                if (!jumped) { pos = cur + 2; jumped = true; }
                if (++jumps > MaxJumps) return false;
                if (ptr >= cur) return false; // pointers go strictly backwards: no loops, no forward references
                cur = ptr;
                continue;
            }
            if ((b & 0xC0) != 0) return false; // reserved label types
            cur++;
            if (cur + b > p.Length) return false;
            wire += b + 1;
            if (wire > MaxNameBytes) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p.Slice(cur, b)));
            cur += b;
        }
        name = sb.ToString();
        return true;
    }
}
