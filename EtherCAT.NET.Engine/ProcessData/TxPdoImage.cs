using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// A typed view over one cyclic exchange's inputs (TxPDO) data image: a byte buffer sized to
/// <see cref="PdoLayout.TotalByteLength"/>, read through named CiA 402 properties instead of magic
/// offsets. Every property resolves its byte offset through <see cref="Layout"/> (computed by
/// <see cref="ProcessImageBuilder"/>), so it stays correct even if a future PDO remap changes entry
/// order.
/// </summary>
public sealed class TxPdoImage
{
    private readonly byte[] _buffer;

    /// <summary>The computed layout this image's field offsets are resolved against.</summary>
    public PdoLayout Layout { get; }

    /// <summary>The raw inputs data image, exactly <see cref="PdoLayout.TotalByteLength"/> bytes — what an LRW datagram's input section carries back.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Creates a zero-initialized image sized to <paramref name="layout"/>.</summary>
    public TxPdoImage(PdoLayout layout)
        : this(layout, new byte[layout.TotalByteLength])
    {
    }

    /// <summary>Wraps an existing buffer (e.g. a slice of a cyclic exchange frame) as a typed image.</summary>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is shorter than <paramref name="layout"/>'s total byte length.</exception>
    public TxPdoImage(PdoLayout layout, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length < layout.TotalByteLength)
        {
            throw new ArgumentException(
                $"Buffer is {buffer.Length} bytes, shorter than the PDO's total byte length of {layout.TotalByteLength}.",
                nameof(buffer));
        }

        Layout = layout;
        _buffer = buffer;
    }

    /// <summary>Error code (CoE 0x603F:00, UINT16).</summary>
    public ushort ErrorCode => BinaryPrimitives.ReadUInt16LittleEndian(FieldAt(0x603F, 0, 2));

    /// <summary>Statusword (CoE 0x6041:00, UINT16) — the DS402 status word decoded by the UI's bit indicators.</summary>
    public ushort Statusword => BinaryPrimitives.ReadUInt16LittleEndian(FieldAt(0x6041, 0, 2));

    /// <summary>Modes of operation display (CoE 0x6061:00, SINT8).</summary>
    public sbyte ModesOfOperationDisplay => unchecked((sbyte)FieldAt(0x6061, 0, 1)[0]);

    /// <summary>Position actual value (CoE 0x6064:00, DINT32) — mirrored into <see cref="RxPdoImage.TargetPosition"/> every cycle in Milestone 1.</summary>
    public int PositionActualValue => BinaryPrimitives.ReadInt32LittleEndian(FieldAt(0x6064, 0, 4));

    /// <summary>Touch probe status (CoE 0x60B9:00, UINT16).</summary>
    public ushort TouchProbeStatus => BinaryPrimitives.ReadUInt16LittleEndian(FieldAt(0x60B9, 0, 2));

    /// <summary>Touch probe pos1 pos value (CoE 0x60BA:00, DINT32).</summary>
    public int TouchProbePos1 => BinaryPrimitives.ReadInt32LittleEndian(FieldAt(0x60BA, 0, 4));

    /// <summary>Following error actual value (CoE 0x60F4:00, DINT32).</summary>
    public int FollowingErrorActualValue => BinaryPrimitives.ReadInt32LittleEndian(FieldAt(0x60F4, 0, 4));

    /// <summary>Digital inputs (CoE 0x60FD:00, UDINT32).</summary>
    public uint DigitalInputs => BinaryPrimitives.ReadUInt32LittleEndian(FieldAt(0x60FD, 0, 4));

    private Span<byte> FieldAt(ushort index, byte subIndex, int expectedByteLength)
    {
        var offset = Layout.GetEntry(index, subIndex).ByteOffset;
        return _buffer.AsSpan(offset, expectedByteLength);
    }
}
