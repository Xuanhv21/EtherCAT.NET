namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// The parsed/composed model of one raw Ethernet II frame carrying an EtherCAT command: a
/// standard Ethernet header (destination MAC, source MAC, EtherType) followed by the 2-byte
/// EtherCAT header and one or more <see cref="EtherCatDatagram"/>s. Built by
/// <see cref="EtherCatFrameBuilder"/> and produced by <see cref="EtherCatFrameParser"/>.
/// </summary>
public sealed class EthernetFrame
{
    /// <summary>The EtherType reserved for EtherCAT (IEC 61158), <c>0x88A4</c>.</summary>
    public const ushort EtherCatEtherType = 0x88A4;

    /// <summary>The EtherCAT header's 4-bit Type field for a normal EtherCAT command frame.</summary>
    public const int EtherCatHeaderTypeCommand = 1;

    /// <summary>Ethernet II header length: destination MAC (6) + source MAC (6) + EtherType (2).</summary>
    public const int HeaderLength = 14;

    /// <summary>Length of the EtherCAT header that follows the Ethernet header.</summary>
    public const int EtherCatHeaderLength = 2;

    /// <summary>
    /// Minimum Ethernet frame length (header through payload, excluding the 4-byte FCS that the
    /// NIC/driver appends). Frames shorter than this must be zero-padded.
    /// </summary>
    public const int MinimumFrameLength = 60;

    /// <summary>Destination MAC address.</summary>
    public MacAddress Destination { get; }

    /// <summary>Source MAC address.</summary>
    public MacAddress Source { get; }

    /// <summary>EtherType; <see cref="EtherCatEtherType"/> for every frame this library produces.</summary>
    public ushort EtherType { get; }

    /// <summary>The datagram(s) carried by this frame, in wire order.</summary>
    public IReadOnlyList<EtherCatDatagram> Datagrams { get; }

    /// <summary>Creates an <see cref="EthernetFrame"/> model.</summary>
    public EthernetFrame(
        MacAddress destination,
        MacAddress source,
        IReadOnlyList<EtherCatDatagram> datagrams,
        ushort etherType = EtherCatEtherType)
    {
        ArgumentNullException.ThrowIfNull(datagrams);

        Destination = destination;
        Source = source;
        Datagrams = datagrams;
        EtherType = etherType;
    }
}
