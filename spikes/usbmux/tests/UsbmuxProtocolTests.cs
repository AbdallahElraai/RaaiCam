using System.Buffers.Binary;
using Xunit;

public class UsbmuxProtocolTests
{
    [Fact] public void Header_IsLittleEndian_WithPlistType()
    {
        var pkt = UsbmuxProtocol.EncodePlistPacket(7, UsbmuxProtocol.ListDevicesRequest());
        Assert.Equal((uint)pkt.Length, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(0)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(8)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(12)));
    }

    [Fact] public void ConnectRequest_PortIsNetworkByteOrder()
        => Assert.Equal(52410, (int)UsbmuxProtocol.ConnectRequest(5, 47820)["PortNumber"]); // 0xBACC -> 0xCCBA

    [Fact] public void RoundTrip_PreservesTagAndKeys()
    {
        var pkt = UsbmuxProtocol.EncodePlistPacket(9, UsbmuxProtocol.ConnectRequest(3, 47820));
        var (tag, plist) = UsbmuxProtocol.DecodePacket(pkt);
        Assert.Equal(9u, tag);
        Assert.Equal(3, Convert.ToInt32(plist["DeviceID"]));
        Assert.Equal("Connect", (string)plist["MessageType"]);
    }

    [Theory, InlineData(0, UsbmuxResult.Ok), InlineData(3, UsbmuxResult.ConnectionRefused), InlineData(2, UsbmuxResult.BadDevice)]
    public void ParseResult_MapsNumbers(int n, UsbmuxResult expected)
    {
        var plist = new Dictionary<string, object> { ["MessageType"] = "Result", ["Number"] = n };
        var decoded = UsbmuxProtocol.DecodePacket(UsbmuxProtocol.EncodePlistPacket(1, plist)).Plist;
        Assert.Equal(expected, UsbmuxProtocol.ParseResult(decoded));
    }
}
