using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Tests.Fakes;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.Tests.Transport;

/// <summary>
/// Verifies <see cref="FakeEthernetFrameTransport"/> honours the <see cref="IEthernetFrameTransport"/>
/// contract deterministically and synchronously: no background thread, <see cref="IEthernetFrameTransport.FrameReceived"/>
/// fires (or doesn't) strictly as a direct consequence of the matching <see cref="IEthernetFrameTransport.Send"/> call.
/// </summary>
public class FakeEthernetFrameTransportTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    [Fact]
    public void Send_raises_FrameReceived_synchronously_with_the_bus_reply()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());
        using IEthernetFrameTransport transport = new FakeEthernetFrameTransport(bus, name: "fake0");

        ReadOnlyMemory<byte>? received = null;
        transport.FrameReceived += (_, frame) => received = frame;

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: new byte[2]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        transport.Send(frame);

        Assert.Equal("fake0", transport.Name);
        Assert.NotNull(received);

        var parsed = EtherCatFrameParser.Parse(received.Value.Span);
        Assert.Equal((ushort)1, parsed.Datagrams[0].WorkingCounter);
    }

    [Fact]
    public void Send_does_not_raise_FrameReceived_when_the_bus_drops_the_frame()
    {
        var bus = new FakeBus { DropAllFrames = true };
        using IEthernetFrameTransport transport = new FakeEthernetFrameTransport(bus);

        var raised = false;
        transport.FrameReceived += (_, _) => raised = true;

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: new byte[2]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        transport.Send(frame);

        Assert.False(raised);
    }

    [Fact]
    public void Send_after_Dispose_throws_ObjectDisposedException()
    {
        var bus = new FakeBus();
        var transport = new FakeEthernetFrameTransport(bus);
        transport.Dispose();

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: new byte[2]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        Assert.Throws<ObjectDisposedException>(() => transport.Send(frame));
    }
}
