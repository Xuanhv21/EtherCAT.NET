using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// The 8-byte Sync Manager configuration block, in the exact wire layout of the ESC register range
/// at <see cref="EscRegisters.SmBaseRegister"/> + n * <see cref="EscRegisters.SmStride"/>:
/// <c>PhysicalStartAddress(2) | Length(2) | ControlByte(1) | StatusByte(1) | Activate(1) | PdiControl(1)</c>,
/// all multi-byte fields little-endian.
/// </summary>
/// <param name="PhysicalStartAddress">First physical ESC register address of this Sync Manager's buffer.</param>
/// <param name="Length">Size in bytes of this Sync Manager's buffer.</param>
/// <param name="ControlByte">
/// The Sync Manager Control register byte, taken <b>verbatim</b> from the ESI file (e.g. 0x26 for
/// SM0, 0x22 for SM1, 0x64 for SM2, 0x20 for SM3 in this plan) — the individual buffer-mode/direction/
/// interrupt sub-bits are intentionally not modelled as separate properties here and must never be
/// recomputed from them; this value is written to the ESC exactly as supplied.
/// </param>
/// <param name="Enable">Activate bit 0: the Sync Manager channel is enabled.</param>
/// <param name="StatusByte">
/// The Status register byte (offset 5). This is an ESC-populated, read-only field (buffer state);
/// callers configuring a Sync Manager should leave this at its default of 0 — <see cref="ToBytes"/>
/// still emits it so <see cref="FromBytes"/> round-trips a block read back from a real/fake ESC exactly.
/// </param>
/// <param name="PdiControl">The PDI Control register byte (offset 7), reserved/PDI-specific; defaults to 0.</param>
public sealed record SmConfig(
    ushort PhysicalStartAddress,
    ushort Length,
    byte ControlByte,
    bool Enable = true,
    byte StatusByte = 0,
    byte PdiControl = 0)
{
    /// <summary>Wire size of one Sync Manager configuration block.</summary>
    public const int ByteLength = 8;

    /// <summary>Serializes this configuration to a new 8-byte array in the exact ESC wire layout.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[ByteLength];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Serializes this configuration into <paramref name="destination"/>, which must be at least <see cref="ByteLength"/> bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException($"Destination must be at least {ByteLength} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[..2], PhysicalStartAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), Length);
        destination[4] = ControlByte;
        destination[5] = StatusByte;
        destination[6] = (byte)(Enable ? 0x01 : 0x00);
        destination[7] = PdiControl;
    }

    /// <summary>Parses an 8-byte ESC wire-format block back into an <see cref="SmConfig"/>.</summary>
    public static SmConfig FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
        {
            throw new ArgumentException($"Source must be at least {ByteLength} bytes (got {bytes.Length}).", nameof(bytes));
        }

        var physicalStart = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        var control = bytes[4];
        var status = bytes[5];
        var activate = bytes[6];
        var pdiControl = bytes[7];

        return new SmConfig(
            physicalStart,
            length,
            control,
            Enable: (activate & 0x01) != 0,
            StatusByte: status,
            PdiControl: pdiControl);
    }
}
