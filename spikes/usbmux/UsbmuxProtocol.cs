using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

public enum UsbmuxResult { Ok = 0, BadCommand = 1, BadDevice = 2, ConnectionRefused = 3, BadVersion = 6 }

/// <summary>usbmuxd plist-protocol wire format: 16-byte LE header + XML plist body.</summary>
public static class UsbmuxProtocol
{
    const int HeaderSize = 16;

    public static byte[] EncodePlistPacket(uint tag, IDictionary<string, object> plist)
    {
        var doc = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), ToXml(plist)));
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, settings)) doc.Save(xw);
        var body = ms.ToArray();

        var pkt = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(0), (uint)pkt.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12), tag);
        body.CopyTo(pkt, HeaderSize);
        return pkt;
    }

    public static (uint Tag, Dictionary<string, object> Plist) DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) throw new InvalidDataException("Packet shorter than header.");
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        using var ms = new MemoryStream(packet[HeaderSize..].ToArray());
        var doc = XDocument.Load(XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }));
        var root = doc.Root?.Elements().FirstOrDefault() ?? throw new InvalidDataException("Empty plist.");
        return (tag, (Dictionary<string, object>)FromXml(root));
    }

    public static Dictionary<string, object> ListDevicesRequest() => new()
    {
        ["MessageType"] = "ListDevices",
        ["ClientVersionString"] = "raaicam-spike",
        ["ProgName"] = "raaicam-spike",
    };

    public static Dictionary<string, object> ConnectRequest(int deviceId, ushort port) => new()
    {
        ["MessageType"] = "Connect",
        ["DeviceID"] = deviceId,
        ["PortNumber"] = (int)BinaryPrimitives.ReverseEndianness(port),
    };

    public static UsbmuxResult ParseResult(Dictionary<string, object> plist)
        => (UsbmuxResult)Convert.ToInt32(plist["Number"], CultureInfo.InvariantCulture);

    static XElement ToXml(object? v) => v switch
    {
        IDictionary<string, object> d => new XElement("dict",
            d.SelectMany(kv => new[] { new XElement("key", kv.Key), ToXml(kv.Value) })),
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        int or long or short or ushort or uint or byte => new XElement("integer", Convert.ToString(v, CultureInfo.InvariantCulture)),
        System.Collections.IEnumerable e => new XElement("array", e.Cast<object>().Select(ToXml)),
        _ => throw new NotSupportedException($"Unsupported plist type {v?.GetType().Name ?? "null"}"),
    };

    static object FromXml(XElement e) => e.Name.LocalName switch
    {
        "dict" => ParseDict(e),
        "string" => e.Value,
        "integer" => long.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "array" => e.Elements().Select(FromXml).ToList(),
        "data" => Convert.FromBase64String(e.Value.Trim()),
        "true" => true,
        "false" => false,
        var n => throw new NotSupportedException($"Unsupported plist element <{n}>"),
    };

    static Dictionary<string, object> ParseDict(XElement e)
    {
        var d = new Dictionary<string, object>();
        var kids = e.Elements().ToList();
        for (int i = 0; i + 1 < kids.Count; i += 2) d[kids[i].Value] = FromXml(kids[i + 1]);
        return d;
    }
}
