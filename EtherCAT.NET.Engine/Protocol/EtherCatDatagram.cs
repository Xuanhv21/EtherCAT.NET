namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// One EtherCAT datagram: <c>Cmd(1) | Idx(1) | Address(4) | Len(11)+Reserved(4)+More(1) (2) |
/// IRQ(2) | Data(n) | WKC(2)</c>, all multi-byte fields little-endian. One or more of these are
/// packed into a single Ethernet frame by <see cref="EtherCatFrameBuilder"/> and split back out by
/// <see cref="EtherCatFrameParser"/>.
/// </summary>
public sealed class EtherCatDatagram
{
    /// <summary>Maximum datagram data length: the Len sub-field is only 11 bits wide.</summary>
    public const int MaxDataLength = 0x07FF;

    /// <summary>Bytes occupied by Cmd + Idx + Address + Len/Reserved/More + IRQ, before the data.</summary>
    public const int HeaderLength = 10;

    /// <summary>Bytes occupied by the trailing Working Counter.</summary>
    public const int WorkingCounterLength = 2;

    /// <summary>The command, which determines how <see cref="Address"/> is interpreted.</summary>
    public EtherCatCommand Command { get; }

    /// <summary>Caller-assigned index, echoed back by slaves unchanged; useful for matching replies to requests.</summary>
    public byte Index { get; }

    /// <summary>Node address (ADP/ADO) or logical address, depending on <see cref="Command"/>.</summary>
    public EtherCatAddress Address { get; }

    /// <summary>Datagram payload. Its length becomes the wire Len field.</summary>
    public byte[] Data { get; }

    /// <summary>Interrupt request mask, normally 0.</summary>
    public ushort Irq { get; }

    /// <summary>
    /// The Working Counter. When building an outbound datagram this is normally 0 (no slave has
    /// touched it yet); when a datagram has been parsed out of a frame received back from the
    /// bus, this is the value the slaves accumulated — the sole basis for deciding whether the
    /// datagram was actually processed.
    /// </summary>
    public ushort WorkingCounter { get; }

    /// <summary>
    /// The wire "More" flag: <c>true</c> when at least one more datagram follows this one in the
    /// same frame. Callers building a datagram do not need to set this themselves —
    /// <see cref="EtherCatFrameBuilder"/> recomputes it from each datagram's position in the list
    /// it is given, overriding whatever is passed here. On a datagram returned by
    /// <see cref="EtherCatFrameParser"/>, this reflects what was actually on the wire.
    /// </summary>
    public bool More { get; }

    /// <summary>Creates a new EtherCAT datagram.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> is longer than <see cref="MaxDataLength"/>.</exception>
    public EtherCatDatagram(
        EtherCatCommand command,
        byte index,
        EtherCatAddress address,
        byte[] data,
        ushort irq = 0,
        ushort workingCounter = 0,
        bool more = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxDataLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                $"Datagram data cannot exceed {MaxDataLength} bytes (11-bit Len field).");
        }

        Command = command;
        Index = index;
        Address = address;
        Data = data;
        Irq = irq;
        WorkingCounter = workingCounter;
        More = more;
    }

    /// <summary>Total bytes this datagram occupies on the wire: header + data + Working Counter.</summary>
    public int WireLength => HeaderLength + Data.Length + WorkingCounterLength;
}
