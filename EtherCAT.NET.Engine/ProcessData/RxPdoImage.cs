using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// A typed view over one cyclic exchange's outputs (RxPDO) data image: a byte buffer sized to
/// <see cref="PdoLayout.TotalByteLength"/>, read and written through named CiA 402 properties
/// instead of magic offsets. Every property resolves its byte offset through <see cref="Layout"/>
/// (computed by <see cref="ProcessImageBuilder"/>), so it stays correct even if a future PDO
/// remap changes entry order.
/// </summary>
public sealed class RxPdoImage
{
    private readonly byte[] _buffer;

    /// <summary>The computed layout this image's field offsets are resolved against.</summary>
    public PdoLayout Layout { get; }

    /// <summary>The raw outputs data image, exactly <see cref="PdoLayout.TotalByteLength"/> bytes — what an LRW datagram's output section carries.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Creates a zero-initialized image sized to <paramref name="layout"/>.</summary>
    public RxPdoImage(PdoLayout layout)
        : this(layout, new byte[layout.TotalByteLength])
    {
    }

    /// <summary>Wraps an existing buffer (e.g. a slice of a cyclic exchange frame) as a typed image.</summary>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is shorter than <paramref name="layout"/>'s total byte length.</exception>
    public RxPdoImage(PdoLayout layout, byte[] buffer)
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

    /// <summary>Controlword (CoE 0x6040:00, UINT16) — the DS402 control command word.</summary>
    public ushort Controlword
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(FieldAt(0x6040, 0, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(FieldAt(0x6040, 0, 2), value);
    }

    /// <summary>Modes of operation (CoE 0x6060:00, SINT8). Held at 0 throughout Milestone 1.</summary>
    public sbyte ModesOfOperation
    {
        get => unchecked((sbyte)FieldAt(0x6060, 0, 1)[0]);
        set => FieldAt(0x6060, 0, 1)[0] = unchecked((byte)value);
    }

    /// <summary>Target position (CoE 0x607A:00, DINT32). Mirrored from <see cref="TxPdoImage.PositionActualValue"/> every cycle in Milestone 1.</summary>
    public int TargetPosition
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(FieldAt(0x607A, 0, 4));
        set => BinaryPrimitives.WriteInt32LittleEndian(FieldAt(0x607A, 0, 4), value);
    }

    /// <summary>Touch probe function (CoE 0x60B8:00, UINT16).</summary>
    public ushort TouchProbeFunction
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(FieldAt(0x60B8, 0, 2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(FieldAt(0x60B8, 0, 2), value);
    }

    private Span<byte> FieldAt(ushort index, byte subIndex, int expectedByteLength)
    {
        var offset = Layout.GetEntry(index, subIndex).ByteOffset;
        return _buffer.AsSpan(offset, expectedByteLength);
    }
}
