using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// The 16-byte FMMU (Fieldbus Memory Management Unit) configuration block, in the exact wire layout
/// of the ESC register range at <see cref="EscRegisters.FmmuBaseRegister"/> + n * <see cref="EscRegisters.FmmuStride"/>:
/// <c>LogicalStartAddress(4) | Length(2) | LogicalStartBit(1) | LogicalStopBit(1) |
/// PhysicalStartAddress(2) | PhysicalStartBit(1) | Reserved(1) | Type(1) | Activate(1) | Reserved(2)</c>,
/// all multi-byte fields little-endian.
/// </summary>
/// <param name="LogicalStartAddress">First logical (process image) byte this FMMU maps, used by LRD/LWR/LRW.</param>
/// <param name="Length">Number of bytes mapped, starting at <paramref name="LogicalStartAddress"/>.</param>
/// <param name="LogicalStartBit">Start bit within the first logical byte (0 for byte-aligned mappings).</param>
/// <param name="LogicalStopBit">Stop bit within the last logical byte (7 for byte-aligned mappings covering whole bytes).</param>
/// <param name="PhysicalStartAddress">First physical ESC register address (normally inside a Sync Manager's memory) this FMMU maps to.</param>
/// <param name="PhysicalStartBit">Start bit within the first physical byte (0 for byte-aligned mappings).</param>
/// <param name="ReadEnabled">Type bit 0: this FMMU participates in logical reads (LRD/LRW read phase) — used for TxPDO (Inputs) FMMUs.</param>
/// <param name="WriteEnabled">Type bit 1: this FMMU participates in logical writes (LWR/LRW write phase) — used for RxPDO (Outputs) FMMUs.</param>
/// <param name="Enable">Activate bit 0: the FMMU is active at all. A configured-but-inactive FMMU is ignored by the ESC.</param>
public sealed record FmmuConfig(
    uint LogicalStartAddress,
    ushort Length,
    byte LogicalStartBit,
    byte LogicalStopBit,
    ushort PhysicalStartAddress,
    byte PhysicalStartBit,
    bool ReadEnabled,
    bool WriteEnabled,
    bool Enable)
{
    /// <summary>Wire size of one FMMU configuration block.</summary>
    public const int ByteLength = 16;

    /// <summary>
    /// Builds a byte-aligned FMMU mapping covering whole bytes (start bit 0, stop bit 7) — the
    /// common case for a PDO region that is itself a whole number of bytes, as every FMMU in this
    /// plan is.
    /// </summary>
    public static FmmuConfig ForByteAlignedRegion(
        uint logicalStartAddress,
        ushort length,
        ushort physicalStartAddress,
        bool readEnabled,
        bool writeEnabled,
        bool enable = true) =>
        new(
            logicalStartAddress,
            length,
            LogicalStartBit: 0,
            LogicalStopBit: 7,
            physicalStartAddress,
            PhysicalStartBit: 0,
            readEnabled,
            writeEnabled,
            enable);

    /// <summary>Serializes this configuration to a new 16-byte array in the exact ESC wire layout.</summary>
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

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], LogicalStartAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), Length);
        destination[6] = LogicalStartBit;
        destination[7] = LogicalStopBit;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2), PhysicalStartAddress);
        destination[10] = PhysicalStartBit;
        destination[11] = 0; // Reserved.
        destination[12] = (byte)((ReadEnabled ? 0x01 : 0x00) | (WriteEnabled ? 0x02 : 0x00));
        destination[13] = (byte)(Enable ? 0x01 : 0x00);
        destination[14] = 0; // Reserved.
        destination[15] = 0; // Reserved.
    }

    /// <summary>Parses a 16-byte ESC wire-format block back into an <see cref="FmmuConfig"/>.</summary>
    public static FmmuConfig FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
        {
            throw new ArgumentException($"Source must be at least {ByteLength} bytes (got {bytes.Length}).", nameof(bytes));
        }

        var logicalStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        var logicalStartBit = bytes[6];
        var logicalStopBit = bytes[7];
        var physicalStart = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2));
        var physicalStartBit = bytes[10];
        var type = bytes[12];
        var activate = bytes[13];

        return new FmmuConfig(
            logicalStart,
            length,
            logicalStartBit,
            logicalStopBit,
            physicalStart,
            physicalStartBit,
            ReadEnabled: (type & 0x01) != 0,
            WriteEnabled: (type & 0x02) != 0,
            Enable: (activate & 0x01) != 0);
    }
}
