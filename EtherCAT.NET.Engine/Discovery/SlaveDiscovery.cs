using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Discovery;

/// <summary>
/// The slave-discovery sequence run once, before PREOP, per the implementation plan's state
/// machine: BRD to count how many slaves are on the bus, APWR to assign each discovered slave a
/// fixed Configured Station Address, and (via <see cref="SlaveIdentity"/>/<see cref="IdentityMatcher"/>)
/// reading and matching its SII identity against a parsed ESI library — the substitute, in this
/// milestone, for a CoE/SDO 0x1018 Identity Object read.
/// </summary>
public static class SlaveDiscovery
{
    /// <summary>
    /// Configured Station Address assigned to the first slave discovered on the bus. Any non-zero
    /// value works (0x0000 is reserved for auto-increment/broadcast addressing); 0x1001 is chosen
    /// arbitrarily and documented here as the single Milestone 1 constant every later step (FMMU/SM
    /// configuration, the AL state machine, cyclic PDO exchange) addresses this slave by.
    /// </summary>
    public const ushort FirstStationAddress = 0x1001;

    /// <summary>
    /// Broadcast-reads the Configured Station Address register (0x0010) from every slave on the bus
    /// and returns how many replied — the datagram's Working Counter. Deliberately does not assume
    /// or assert any particular count: Milestone 1's test rig happens to wire up exactly one slave,
    /// but this method reports whatever is actually out there so a caller can decide what to do
    /// with 0, 1, or many.
    /// </summary>
    /// <exception cref="EscCommunicationException">No reply was observed at all.</exception>
    public static int CountSlaves(EscClient escClient)
    {
        ArgumentNullException.ThrowIfNull(escClient);

        var (_, slaveCount) = escClient.BroadcastRead(EscRegisters.ConfiguredStationAddressRegister, length: 2);
        return slaveCount;
    }

    /// <summary>
    /// Assigns <paramref name="stationAddress"/> as the Configured Station Address of the slave at
    /// auto-increment ring position <paramref name="autoIncrementAddress"/> (0 for the first slave
    /// on the bus) via APWR to register 0x0010 with ADP = <paramref name="autoIncrementAddress"/>.
    /// </summary>
    /// <exception cref="EscWorkingCounterException">No slave sits at that ring position.</exception>
    public static void AssignStationAddress(EscClient escClient, ushort stationAddress = FirstStationAddress, ushort autoIncrementAddress = 0)
    {
        ArgumentNullException.ThrowIfNull(escClient);

        escClient.ConfigureStationAddress(autoIncrementAddress, stationAddress);
    }

    /// <summary>
    /// Runs the full Milestone 1 discovery sequence for a single-slave bus: counts slaves, assigns
    /// <paramref name="stationAddress"/> to the slave at auto-increment position 0, reads its SII
    /// identity, and matches that identity against <paramref name="esiLibrary"/>.
    /// </summary>
    /// <exception cref="EscCommunicationException">No reply was observed to the initial BRD.</exception>
    /// <exception cref="EscWorkingCounterException">No slave sits at auto-increment position 0, or a later register access failed.</exception>
    /// <exception cref="SlaveIdentityMismatchException">The discovered identity matches no device in <paramref name="esiLibrary"/>.</exception>
    public static DiscoveryResult DiscoverSingleSlave(EscClient escClient, EsiDeviceLibrary esiLibrary, ushort stationAddress = FirstStationAddress)
    {
        ArgumentNullException.ThrowIfNull(escClient);
        ArgumentNullException.ThrowIfNull(esiLibrary);

        var slaveCount = CountSlaves(escClient);
        AssignStationAddress(escClient, stationAddress);

        var identity = SlaveIdentity.Read(escClient, stationAddress);
        var device = IdentityMatcher.Match(identity, esiLibrary);

        return new DiscoveryResult(slaveCount, stationAddress, identity, device);
    }
}

/// <summary>Outcome of <see cref="SlaveDiscovery.DiscoverSingleSlave"/>.</summary>
/// <param name="SlaveCount">How many slaves answered the initial BRD (see <see cref="SlaveDiscovery.CountSlaves"/>).</param>
/// <param name="StationAddress">The Configured Station Address assigned to the slave this result describes.</param>
/// <param name="Identity">The slave's raw SII identity fields.</param>
/// <param name="Device">The matched ESI device descriptor — its SyncManagers/Fmmus/RxPdos/TxPdos drive every later configuration step.</param>
public sealed record DiscoveryResult(int SlaveCount, ushort StationAddress, SlaveIdentity Identity, EsiDeviceDescriptor Device);
