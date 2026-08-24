using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.Discovery;

/// <summary>
/// The three SII EEPROM identity fields read off a slave during discovery and used by
/// <see cref="IdentityMatcher"/> to find the matching <see cref="Esi.EsiDeviceDescriptor"/> in a
/// parsed ESI library — the Milestone 1 substitute for a CoE/SDO 0x1018 Identity Object read, which
/// would require a mailbox client this milestone does not implement.
/// </summary>
/// <param name="VendorId">SII EEPROM word 0x0008.</param>
/// <param name="ProductCode">SII EEPROM word 0x000A.</param>
/// <param name="RevisionNumber">SII EEPROM word 0x000C.</param>
public sealed record SlaveIdentity(uint VendorId, uint ProductCode, uint RevisionNumber)
{
    /// <summary>
    /// Reads the Vendor Id / Product Code / Revision Number fields out of the SII EEPROM of the
    /// slave at <paramref name="stationAddress"/>, via <see cref="SiiEeprom"/> over
    /// <paramref name="escClient"/> (the write-address / write-read-command / poll-busy / read-data
    /// sequence against registers 0x0502/0x0504/0x0508).
    /// </summary>
    /// <exception cref="EscWorkingCounterException">Any underlying register access returned an unexpected Working Counter.</exception>
    /// <exception cref="TimeoutException">The SII Busy bit never cleared within <see cref="SiiEeprom.MaxPollAttempts"/> polls.</exception>
    public static SlaveIdentity Read(EscClient escClient, ushort stationAddress)
    {
        ArgumentNullException.ThrowIfNull(escClient);

        var sii = new SiiEeprom(escClient, stationAddress);
        return new SlaveIdentity(sii.ReadVendorId(), sii.ReadProductCode(), sii.ReadRevisionNumber());
    }
}
