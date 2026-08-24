using EtherCAT.NET.Engine.Protocol;

namespace EtherCAT.NET.Engine.Tests.Protocol;

/// <summary>
/// Round-trips <see cref="EtherCatFrameBuilder"/> output back through <see cref="EtherCatFrameParser"/>
/// and asserts every field survives, plus the Ethernet minimum-frame-length padding rules described
/// in the implementation plan (Protocol/ frame layer).
/// </summary>
public class EtherCatFrameRoundTripTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    [Fact]
    public void Roundtrips_a_single_BRD_datagram_and_pads_the_frame_to_60_bytes()
    {
        var data = new byte[] { 0x00, 0x00 };
        var datagram = new EtherCatDatagram(
            EtherCatCommand.Brd,
            index: 0x11,
            EtherCatAddress.ForNodeAddressed(adp: 0x0000, ado: 0x0000),
            data,
            irq: 0x1234);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        // Ethernet(14) + EtherCAT header(2) + datagram header(10) + data(2) + WKC(2) = 30 -> padded to 60.
        Assert.Equal(60, frame.Length);
        AssertTrailingBytesAreZero(frame, fromInclusive: 30);

        var parsed = EtherCatFrameParser.Parse(frame);

        Assert.Equal(MacAddress.Broadcast, parsed.Destination);
        Assert.Equal(TestSourceMac, parsed.Source);
        Assert.Equal(EthernetFrame.EtherCatEtherType, parsed.EtherType);
        Assert.Single(parsed.Datagrams);

        var parsedDatagram = parsed.Datagrams[0];
        Assert.Equal(EtherCatCommand.Brd, parsedDatagram.Command);
        Assert.Equal(0x11, parsedDatagram.Index);
        Assert.Equal((ushort)0x0000, parsedDatagram.Address.Adp);
        Assert.Equal((ushort)0x0000, parsedDatagram.Address.Ado);
        Assert.Equal(data, parsedDatagram.Data);
        Assert.Equal((ushort)0x1234, parsedDatagram.Irq);
        Assert.Equal((ushort)0, parsedDatagram.WorkingCounter);
        Assert.False(parsedDatagram.More);
    }

    [Fact]
    public void Roundtrips_a_single_FPWR_datagram_writing_AL_control()
    {
        // FPWR to Configured Station Address 0x0001, register 0x0120 (AL Control), value 0x0002 (PREOP).
        var data = new byte[] { 0x02, 0x00 };
        var datagram = new EtherCatDatagram(
            EtherCatCommand.Fpwr,
            index: 0x42,
            EtherCatAddress.ForNodeAddressed(adp: 0x0001, ado: 0x0120),
            data,
            irq: 0,
            workingCounter: 0);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        Assert.Equal(60, frame.Length);
        AssertTrailingBytesAreZero(frame, fromInclusive: 30);

        var parsed = EtherCatFrameParser.Parse(frame);
        Assert.Single(parsed.Datagrams);

        var parsedDatagram = parsed.Datagrams[0];
        Assert.Equal(EtherCatCommand.Fpwr, parsedDatagram.Command);
        Assert.Equal(0x42, parsedDatagram.Index);
        Assert.Equal((ushort)0x0001, parsedDatagram.Address.Adp);
        Assert.Equal((ushort)0x0120, parsedDatagram.Address.Ado);
        Assert.Equal(data, parsedDatagram.Data);
        Assert.False(parsedDatagram.More);
    }

    [Fact]
    public void LRW_datagram_with_9_output_and_23_input_bytes_produces_exactly_60_bytes_with_no_padding()
    {
        // 9 bytes RxPDO output + 23 bytes TxPDO input = 32 bytes of PDO data in one LRW datagram.
        var data = new byte[32];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        var datagram = new EtherCatDatagram(
            EtherCatCommand.Lrw,
            index: 0x01,
            EtherCatAddress.ForLogicalAddressed(0x00000000),
            data);

        // Plan's own arithmetic: 14 (Ethernet) + 2 (EtherCAT header) + 10 (datagram header) + 32
        // (data) + 2 (WKC) = 60 exactly, i.e. this is the boundary where zero extra padding bytes
        // are needed — not 59 (would need 1 byte of padding) and not 61 (would need none either,
        // but the frame itself would already exceed the minimum).
        var unpaddedLength = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength + datagram.WireLength;
        Assert.Equal(60, unpaddedLength);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        Assert.Equal(60, frame.Length);

        var parsed = EtherCatFrameParser.Parse(frame);
        Assert.Single(parsed.Datagrams);

        var parsedDatagram = parsed.Datagrams[0];
        Assert.Equal(EtherCatCommand.Lrw, parsedDatagram.Command);
        Assert.Equal(0x01, parsedDatagram.Index);
        Assert.Equal(0x00000000u, parsedDatagram.Address.LogicalAddress);
        Assert.Equal(data, parsedDatagram.Data);
        Assert.False(parsedDatagram.More);
    }

    [Fact]
    public void Chaining_two_datagrams_sets_More_on_all_but_the_last_and_both_parse_back()
    {
        var first = new EtherCatDatagram(
            EtherCatCommand.Fprd,
            index: 0x01,
            EtherCatAddress.ForNodeAddressed(adp: 0x0001, ado: 0x0130),
            data: [0xAA, 0xBB]);

        var second = new EtherCatDatagram(
            EtherCatCommand.Fprd,
            index: 0x02,
            EtherCatAddress.ForNodeAddressed(adp: 0x0001, ado: 0x0134),
            data: [0xCC]);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [first, second]);

        var parsed = EtherCatFrameParser.Parse(frame);

        Assert.Equal(2, parsed.Datagrams.Count);

        var parsedFirst = parsed.Datagrams[0];
        Assert.True(parsedFirst.More, "The first of two chained datagrams must have More=1.");
        Assert.Equal(EtherCatCommand.Fprd, parsedFirst.Command);
        Assert.Equal(0x01, parsedFirst.Index);
        Assert.Equal((ushort)0x0130, parsedFirst.Address.Ado);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, parsedFirst.Data);

        var parsedSecond = parsed.Datagrams[1];
        Assert.False(parsedSecond.More, "The last of two chained datagrams must have More=0.");
        Assert.Equal(EtherCatCommand.Fprd, parsedSecond.Command);
        Assert.Equal(0x02, parsedSecond.Index);
        Assert.Equal((ushort)0x0134, parsedSecond.Address.Ado);
        Assert.Equal(new byte[] { 0xCC }, parsedSecond.Data);
    }

    [Fact]
    public void More_flag_passed_into_the_datagram_constructor_is_ignored_by_the_builder()
    {
        // The builder must derive More from position in the list, not trust whatever the caller
        // happened to pass into each EtherCatDatagram.
        var first = new EtherCatDatagram(
            EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: [], more: false);
        var second = new EtherCatDatagram(
            EtherCatCommand.Brd, 0x02, EtherCatAddress.ForNodeAddressed(0, 0), data: [], more: true);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [first, second]);
        var parsed = EtherCatFrameParser.Parse(frame);

        Assert.True(parsed.Datagrams[0].More);
        Assert.False(parsed.Datagrams[1].More);
    }

    private static void AssertTrailingBytesAreZero(byte[] frame, int fromInclusive)
    {
        for (var i = fromInclusive; i < frame.Length; i++)
        {
            Assert.Equal(0, frame[i]);
        }
    }
}
