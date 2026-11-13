using System.Collections;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RaaiCam.Core.Usbmux;

/// <summary>The daemon sent something that is not a valid usbmux packet or plist.</summary>
public sealed class UsbmuxProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal, safe XML plist reader/writer (dict, array, string, integer, real, true/false, data).
/// DTDs are ignored and no resolver is set, so external entities are never fetched.
/// Parsed values: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, double, bool, byte[].
/// </summary>
internal static class Plist
{
    const int MaxDepth = 32;

    public static object? Parse(ReadOnlySpan<byte> xml)
    {
        try
        {
            using var ms = new MemoryStream(xml.ToArray());
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 0 };
            using var reader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is not { Name.LocalName: "plist" } root || root.Elements().FirstOrDefault() is not { } first)
                throw new UsbmuxProtocolException("Plist has no root value.");
            return FromXml(first, 0);
        }
        catch (XmlException e) { throw new UsbmuxProtocolException("Malformed plist XML.", e); }
        catch (FormatException e) { throw new UsbmuxProtocolException("Malformed plist value.", e); }
        catch (InvalidOperationException e) { throw new UsbmuxProtocolException("Malformed plist.", e); }
    }

    public static byte[] Write(IDictionary<string, object?> dict)
    {
        var doc = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), ToXml(dict)));
        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(xw);
        return ms.ToArray();
    }

    static XElement ToXml(object? v) => v switch
    {
        IDictionary<string, object?> d => new XElement("dict",
            d.SelectMany(kv => new[] { new XElement("key", kv.Key), ToXml(kv.Value) })),
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        double r => new XElement("real", r.ToString("R", CultureInfo.InvariantCulture)),
        int or long or short or ushort or uint or byte => new XElement("integer", Convert.ToString(v, CultureInfo.InvariantCulture)),
        IEnumerable e => new XElement("array", e.Cast<object?>().Select(ToXml)),
        _ => throw new NotSupportedException($"Unsupported plist type {v?.GetType().Name ?? "null"}"),
    };

    static object? FromXml(XElement e, int depth)
    {
        if (depth > MaxDepth) throw new UsbmuxProtocolException("Plist nested too deeply.");
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var kids = e.Elements().ToList();
                if (kids.Count % 2 != 0) throw new UsbmuxProtocolException("Plist dict has a key without a value.");
                var d = new Dictionary<string, object?>();
                for (int i = 0; i < kids.Count; i += 2)
                {
                    if (kids[i].Name.LocalName != "key") throw new UsbmuxProtocolException("Plist dict expected <key>.");
                    d[kids[i].Value] = FromXml(kids[i + 1], depth + 1);
                }
                return d;
            }
            case "array": return e.Elements().Select(c => FromXml(c, depth + 1)).ToList();
            case "string": return e.Value;
            case "integer":
                return long.TryParse(e.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
                    ? l : throw new UsbmuxProtocolException("Plist integer is not a number.");
            case "real":
                return double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                    ? r : throw new UsbmuxProtocolException("Plist real is not a number.");
            case "data":
                return Convert.FromBase64String(e.Value.Trim());
            case "true": return true;
            case "false": return false;
            default: throw new UsbmuxProtocolException($"Unsupported plist element <{e.Name.LocalName}>.");
        }
    }
}
