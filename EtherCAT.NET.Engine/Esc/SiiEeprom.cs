using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// Reads 32-bit fields out of a slave's SII EEPROM through the register-mapped access interface at
/// <see cref="EscRegisters.SiiControlRegister"/> (0x0502) / <see cref="EscRegisters.SiiAddressRegister"/>
/// (0x0504) / <see cref="EscRegisters.SiiDataRegister"/> (0x0508), used during discovery to fetch the
/// Vendor Id / Product Code / Revision Number / Serial Number identity fields for comparison against
/// the parsed ESI model (in place of a full CoE/SDO 0x1018 read, which Milestone 1 does not
/// implement). One word address read is:
/// <list type="number">
/// <item>write the target word address to the Address register (0x0504);</item>
/// <item>write a read-request command to the Control register (0x0502);</item>
/// <item>poll the Control register until its Busy bit (bit 15) clears;</item>
/// <item>read the result out of the Data register (0x0508).</item>
/// </list>
/// </summary>
public sealed class SiiEeprom
{
    /// <summary>SII word address of the 4-byte Vendor Id field.</summary>
    public const ushort VendorIdWordAddress = 0x0008;

    /// <summary>SII word address of the 4-byte Product Code field.</summary>
    public const ushort ProductCodeWordAddress = 0x000A;

    /// <summary>SII word address of the 4-byte Revision Number field.</summary>
    public const ushort RevisionNumberWordAddress = 0x000C;

    /// <summary>SII word address of the 4-byte Serial Number field.</summary>
    public const ushort SerialNumberWordAddress = 0x000E;

    /// <summary>SII Control register command bit that requests a read (bit 8).</summary>
    private const ushort ReadCommandBit = 0x0100;

    /// <summary>SII Control register Busy flag (bit 15).</summary>
    private const ushort BusyBit = 0x8000;

    private readonly EscClient _escClient;
    private readonly ushort _stationAddress;

    /// <summary>
    /// Creates a helper that reads the SII EEPROM of the slave at <paramref name="stationAddress"/>
    /// using <paramref name="escClient"/>.
    /// </summary>
    public SiiEeprom(EscClient escClient, ushort stationAddress)
    {
        ArgumentNullException.ThrowIfNull(escClient);
        _escClient = escClient;
        _stationAddress = stationAddress;
    }

    /// <summary>
    /// Maximum number of times to poll the Control register's Busy bit before giving up. Exposed so
    /// tests can force the timeout path without an actually-slow simulated slave; a real ESC clears
    /// Busy within microseconds, so the default is generous.
    /// </summary>
    public int MaxPollAttempts { get; set; } = 1000;

    /// <summary>
    /// Reads the 4-byte field at <paramref name="wordAddress"/>, running the full write-address /
    /// write-read-command / poll-busy / read-data sequence described on this type.
    /// </summary>
    /// <exception cref="TimeoutException">The Busy bit never cleared within <see cref="MaxPollAttempts"/> polls.</exception>
    /// <exception cref="EscWorkingCounterException">Any of the underlying register accesses returned an unexpected Working Counter.</exception>
    public uint ReadUInt32(ushort wordAddress)
    {
        Span<byte> addressBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(addressBytes, wordAddress);
        _escClient.WriteRegister(_stationAddress, EscRegisters.SiiAddressRegister, addressBytes);

        Span<byte> controlBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(controlBytes, ReadCommandBit);
        _escClient.WriteRegister(_stationAddress, EscRegisters.SiiControlRegister, controlBytes);

        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            var status = BinaryPrimitives.ReadUInt16LittleEndian(_escClient.ReadRegister(_stationAddress, EscRegisters.SiiControlRegister, 2));
            if ((status & BusyBit) == 0)
            {
                var data = _escClient.ReadRegister(_stationAddress, EscRegisters.SiiDataRegister, 4);
                return BinaryPrimitives.ReadUInt32LittleEndian(data);
            }
        }

        throw new TimeoutException(
            $"SII EEPROM read of word 0x{wordAddress:X4} on station 0x{_stationAddress:X4} did not complete within {MaxPollAttempts} poll attempts.");
    }

    /// <summary>Reads the Vendor Id field (SII word 0x0008).</summary>
    public uint ReadVendorId() => ReadUInt32(VendorIdWordAddress);

    /// <summary>Reads the Product Code field (SII word 0x000A).</summary>
    public uint ReadProductCode() => ReadUInt32(ProductCodeWordAddress);

    /// <summary>Reads the Revision Number field (SII word 0x000C).</summary>
    public uint ReadRevisionNumber() => ReadUInt32(RevisionNumberWordAddress);

    /// <summary>Reads the Serial Number field (SII word 0x000E).</summary>
    public uint ReadSerialNumber() => ReadUInt32(SerialNumberWordAddress);
}
