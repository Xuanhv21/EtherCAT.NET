using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// Packs one or more <see cref="EtherCatDatagram"/>s into a single raw Ethernet II frame: Ethernet
/// header, 2-byte EtherCAT header, the datagrams back-to-back (each with a correctly-computed
/// More bit), and zero padding up to the Ethernet minimum frame length when the result would
/// otherwise be shorter.
/// </summary>
public static class EtherCatFrameBuilder
{
    /// <summary>
    /// Builds a raw Ethernet frame carrying <paramref name="datagrams"/> as a single EtherCAT
    /// command frame.
    /// </summary>
    /// <param name="destination">Destination MAC address (typically <see cref="MacAddress.Broadcast"/>).</param>
    /// <param name="source">Source MAC address (the sending NIC's own address).</param>
    /// <param name="datagrams">
    /// One or more datagrams to pack, in the order they should be processed by slaves. The More
    /// bit is set on every datagram except the last, regardless of what was passed to each
    /// datagram's own constructor.
    /// </param>
    /// <returns>
    /// The complete raw frame, ready to hand to a transport, zero-padded up to
    /// <see cref="EthernetFrame.MinimumFrameLength"/> bytes when needed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="datagrams"/> is empty, or the combined datagram length overflows the
    /// 11-bit EtherCAT header Length field.
    /// </exception>
    public static byte[] Build(MacAddress destination, MacAddress source, IReadOnlyList<EtherCatDatagram> datagrams)
    {
        ArgumentNullException.ThrowIfNull(datagrams);
        if (datagrams.Count == 0)
        {
            throw new ArgumentException("At least one datagram is required.", nameof(datagrams));
        }

        var ecatPayloadLength = 0;
        foreach (var datagram in datagrams)
        {
            ecatPayloadLength += datagram.WireLength;
        }

        if (ecatPayloadLength > 0x07FF)
        {
            throw new ArgumentException(
                $"Combined datagram length {ecatPayloadLength} exceeds the 11-bit EtherCAT header Length field (max 2047).",
                nameof(datagrams));
        }

        var unpaddedLength = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength + ecatPayloadLength;
        var frameLength = Math.Max(unpaddedLength, EthernetFrame.MinimumFrameLength);

        // The array is zero-initialized by the runtime, which is exactly the padding we need for
        // any bytes beyond unpaddedLength.
        var frame = new byte[frameLength];
        var span = frame.AsSpan();

        destination.WriteTo(span[..6]);
        source.WriteTo(span.Slice(6, 6));

        // EtherType is a standard Ethernet II field and, unlike every EtherCAT field, is
        // transmitted big-endian (network byte order).
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(12, 2), EthernetFrame.EtherCatEtherType);

        ushort headerWord = (ushort)(ecatPayloadLength & 0x07FF);
        headerWord |= (ushort)(EthernetFrame.EtherCatHeaderTypeCommand << 12);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), headerWord);

        var offset = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength;
        for (var i = 0; i < datagrams.Count; i++)
        {
            var datagram = datagrams[i];
            var more = i < datagrams.Count - 1;
            offset += WriteDatagram(span[offset..], datagram, more);
        }

        return frame;
    }

    private static int WriteDatagram(Span<byte> destination, EtherCatDatagram datagram, bool more)
    {
        destination[0] = (byte)datagram.Command;
        destination[1] = datagram.Index;

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), datagram.Address.Adp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), datagram.Address.Ado);

        ushort lenWord = (ushort)(datagram.Data.Length & 0x07FF);
        if (more)
        {
            lenWord |= 0x8000; // bit 15 = More; bits 11-14 stay 0 (reserved).
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), lenWord);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2), datagram.Irq);

        datagram.Data.AsSpan().CopyTo(destination.Slice(EtherCatDatagram.HeaderLength, datagram.Data.Length));

        var wkcOffset = EtherCatDatagram.HeaderLength + datagram.Data.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(wkcOffset, 2), datagram.WorkingCounter);

        return datagram.WireLength;
    }
}
