namespace EtherCAT.NET.Engine.Transport;

/// <summary>
/// Abstraction over "something that can send and receive raw Ethernet frames carrying EtherCAT
/// datagrams" — a real NIC (<c>PcapEthernetFrameTransport</c> in
/// <c>EtherCAT.NET.Transport.Pcap</c>, wrapping a SharpPcap <c>ICaptureDevice</c>) or, for tests,
/// <c>FakeEthernetFrameTransport</c> wrapping a <c>FakeBus</c> of <c>FakeSlaveDevice</c>s. Everything
/// above this layer (<see cref="EtherCAT.NET.Engine.Protocol"/>, <c>Esc/</c>, <c>StateMachine/</c>,
/// <c>ProcessData/</c>) only ever depends on this interface, never on SharpPcap or the fakes
/// directly — that is what lets <c>EtherCAT.NET.Engine</c> and its tests build and run on a machine
/// without Npcap installed.
/// </summary>
public interface IEthernetFrameTransport : IDisposable
{
    /// <summary>A human-readable identifier for this transport (e.g. the underlying NIC's name).</summary>
    string Name { get; }

    /// <summary>
    /// Sends one raw Ethernet frame (as produced by <see cref="EtherCAT.NET.Engine.Protocol.EtherCatFrameBuilder"/>)
    /// out onto the wire (or, for a fake transport, into the fake bus).
    /// </summary>
    void Send(ReadOnlyMemory<byte> frame);

    /// <summary>
    /// Raised whenever a raw Ethernet frame is received. For a real transport this fires from a
    /// capture callback for every frame the NIC sees (callers are expected to filter by EtherType/
    /// content themselves); for <c>FakeEthernetFrameTransport</c> it fires synchronously and
    /// deterministically, right after <see cref="Send"/> hands the frame to the fake bus, carrying
    /// the bus's reply.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;
}
