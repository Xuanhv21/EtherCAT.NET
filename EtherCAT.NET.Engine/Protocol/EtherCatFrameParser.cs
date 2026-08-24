using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// The inverse of <see cref="EtherCatFrameBuilder"/>: splits a raw Ethernet frame received back
/// from the bus into its Ethernet header and EtherCAT datagram(s), exposing each datagram's
/// Working Counter so the caller can decide whether it was actually processed by a slave.
/// </summary>
public static class EtherCatFrameParser
{
    /// <summary>Parses a raw Ethernet frame into an <see cref="EthernetFrame"/> model.</summary>
    /// <exception cref="ArgumentException">The frame is too short to contain an Ethernet + EtherCAT header.</exception>
    /// <exception cref="FormatException">
    /// The EtherCAT header's Type field is not <see cref="EthernetFrame.EtherCatHeaderTypeCommand"/>,
    /// or a datagram is truncated relative to what its own length fields declare.
    /// </exception>
    public static EthernetFrame Parse(ReadOnlySpan<byte> frame)
    {
        var minimumHeaderLength = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength;
        if (frame.Length < minimumHeaderLength)
        {
            throw new ArgumentException(
                $"Frame is only {frame.Length} bytes; an Ethernet + EtherCAT header needs at least {minimumHeaderLength}.",
                nameof(frame));
        }

        var destination = new MacAddress(frame[..6]);
        var source = new MacAddress(frame.Slice(6, 6));
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));

        var headerWord = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(14, 2));
        var ecatLength = headerWord & 0x07FF;
        var type = (headerWord >> 12) & 0x0F;

        if (type != EthernetFrame.EtherCatHeaderTypeCommand)
        {
            throw new FormatException(
                $"Unexpected EtherCAT header Type field 0x{type:X} (expected 0x{EthernetFrame.EtherCatHeaderTypeCommand:X}).");
        }

        var available = frame.Length - minimumHeaderLength;
        if (ecatLength > available)
        {
            throw new FormatException(
                $"EtherCAT header declares {ecatLength} bytes of datagrams but only {available} bytes remain in the frame.");
        }

        var datagrams = new List<EtherCatDatagram>();
        var offset = minimumHeaderLength;
        var remaining = ecatLength;

        while (remaining > 0)
        {
            var minimumDatagramLength = EtherCatDatagram.HeaderLength + EtherCatDatagram.WorkingCounterLength;
            if (remaining < minimumDatagramLength)
            {
                throw new FormatException(
                    $"Truncated EtherCAT datagram: {remaining} bytes remain, need at least {minimumDatagramLength} for header + WKC.");
            }

            var command = (EtherCatCommand)frame[offset];
            var index = frame[offset + 1];
            var adp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset + 2, 2));
            var ado = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset + 4, 2));
            var address = EtherCatAddress.ForNodeAddressed(adp, ado);

            var lenWord = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset + 6, 2));
            var dataLength = lenWord & 0x07FF;
            var more = (lenWord & 0x8000) != 0;

            var irq = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset + 8, 2));

            var datagramLength = EtherCatDatagram.HeaderLength + dataLength + EtherCatDatagram.WorkingCounterLength;
            if (datagramLength > remaining)
            {
                throw new FormatException(
                    $"Truncated EtherCAT datagram: declared data length {dataLength} needs {datagramLength} bytes but only {remaining} remain.");
            }

            var dataOffset = offset + EtherCatDatagram.HeaderLength;
            var data = frame.Slice(dataOffset, dataLength).ToArray();

            var wkcOffset = dataOffset + dataLength;
            var workingCounter = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(wkcOffset, 2));

            datagrams.Add(new EtherCatDatagram(command, index, address, data, irq, workingCounter, more));

            offset += datagramLength;
            remaining -= datagramLength;
        }

        return new EthernetFrame(destination, source, datagrams, etherType);
    }
}
