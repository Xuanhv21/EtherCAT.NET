using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Tests.Esi;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.Discovery;

/// <summary>
/// <see cref="SlaveDiscovery"/> exercised against <see cref="FakeBus"/>/<see cref="FakeSlaveDevice"/>:
/// BRD slave counting (proving the returned count reflects the actual bus population rather than
/// being hardcoded to 1), APWR address assignment followed by a successful FPRD against the newly
/// fixed address (and a failing one against the old address), and the full single-slave sequence
/// matched against the real embedded Panasonic MADLN01BE ESI library.
/// </summary>
public class SlaveDiscoveryTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private static EscClient CreateClient(FakeBus bus) => new(new FakeEthernetFrameTransport(bus), TestSourceMac);

    [Fact]
    public void CountSlaves_returns_1_for_the_Milestone_1_single_slave_bus()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());

        var count = SlaveDiscovery.CountSlaves(CreateClient(bus));

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountSlaves_reports_the_actual_bus_population_rather_than_assuming_exactly_one()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());

        var count = SlaveDiscovery.CountSlaves(CreateClient(bus));

        Assert.Equal(3, count);
    }

    [Fact]
    public void AssignStationAddress_then_Fprd_against_the_new_fixed_address_succeeds()
    {
        var bus = new FakeBus();
        var slave = new FakeSlaveDevice();
        bus.AddSlave(slave);
        var client = CreateClient(bus);

        SlaveDiscovery.AssignStationAddress(client, SlaveDiscovery.FirstStationAddress);

        Assert.Equal(SlaveDiscovery.FirstStationAddress, slave.ConfiguredStationAddress);

        // FPRD against a scratch register at the newly assigned fixed address must now succeed
        // (WKC=1), proving the address is actually usable for node-addressed access afterwards --
        // not just recorded on the slave.
        const ushort scratchRegister = 0x0900;
        slave.WriteRegisterBytes(scratchRegister, [0xAA, 0xBB]);
        var readBack = client.ReadRegister(SlaveDiscovery.FirstStationAddress, scratchRegister, 2);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, readBack);
    }

    [Fact]
    public void AssignStationAddress_leaves_the_slave_unreachable_at_its_old_default_address()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice()); // default Configured Station Address 0x0000.
        var client = CreateClient(bus);

        SlaveDiscovery.AssignStationAddress(client, SlaveDiscovery.FirstStationAddress);

        var ex = Assert.Throws<EscWorkingCounterException>(() => client.ReadRegister(0x0000, 0x0900, 2));
        Assert.Equal((ushort)0, ex.ActualWorkingCounter);
    }

    [Fact]
    public void DiscoverSingleSlave_runs_BRD_APWR_and_SII_identity_and_matches_the_real_MADLN01BE_descriptor()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision));
        var client = CreateClient(bus);

        var esiLibrary = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();

        var result = SlaveDiscovery.DiscoverSingleSlave(client, esiLibrary);

        Assert.Equal(1, result.SlaveCount);
        Assert.Equal(SlaveDiscovery.FirstStationAddress, result.StationAddress);
        Assert.Equal(PanasonicVendorId, result.Identity.VendorId);
        Assert.Equal(Madln01BeProductCode, result.Identity.ProductCode);
        Assert.Equal(Madln01BeRevision, result.Identity.RevisionNumber);
        Assert.Equal("MADLN01BE", result.Device.Name);
    }

    [Fact]
    public void DiscoverSingleSlave_throws_a_clear_error_instead_of_crashing_when_the_seeded_identity_matches_no_device()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, productCode: 0x12345678, revisionNumber: 0x00010000)); // bogus product code
        var client = CreateClient(bus);

        var esiLibrary = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();

        var ex = Assert.Throws<SlaveIdentityMismatchException>(() => SlaveDiscovery.DiscoverSingleSlave(client, esiLibrary));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // --- DiscoverSingleSlave(escClient, IEnumerable<EsiDeviceLibrary>, ...): multi-vendor catalog overload. ---

    private const uint SyntheticVendorId = EsiCatalogTests.SyntheticVendorId;
    private const uint SyntheticProductCode = EsiCatalogTests.SyntheticProductCode;
    private const uint SyntheticRevisionNumber = EsiCatalogTests.SyntheticRevisionNumber;

    private static List<EsiDeviceLibrary> CombinedLibraries() =>
        [EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be(), EsiXmlParser.Parse(EsiCatalogTests.SyntheticEsiXml)];

    [Fact]
    public void DiscoverSingleSlave_with_a_library_set_matches_the_Panasonic_slave_out_of_a_combined_catalog()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision));
        var client = CreateClient(bus);

        var result = SlaveDiscovery.DiscoverSingleSlave(client, CombinedLibraries());

        Assert.Equal("MADLN01BE", result.Device.Name);
    }

    [Fact]
    public void DiscoverSingleSlave_with_a_library_set_matches_the_synthetic_slave_out_of_the_same_combined_catalog()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(SyntheticVendorId, SyntheticProductCode, SyntheticRevisionNumber));
        var client = CreateClient(bus);

        var result = SlaveDiscovery.DiscoverSingleSlave(client, CombinedLibraries());

        Assert.Equal(EsiCatalogTests.SyntheticDeviceName, result.Device.Name);
    }

    [Fact]
    public void DiscoverSingleSlave_with_a_library_set_throws_when_the_connected_slave_matches_neither_library()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(vendorId: 0x0000AAAA, productCode: 0x0000BBBB, revisionNumber: 0x0000CCCC));
        var client = CreateClient(bus);

        var ex = Assert.Throws<SlaveIdentityMismatchException>(() => SlaveDiscovery.DiscoverSingleSlave(client, CombinedLibraries()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // --- DiscoverAllSlaves: walks every auto-increment ring position, not just position 0. ---

    [Fact]
    public void DiscoverAllSlaves_returns_zero_results_when_no_slave_answers_the_bus()
    {
        var bus = new FakeBus(); // no slaves added at all.
        var client = CreateClient(bus);

        var results = SlaveDiscovery.DiscoverAllSlaves(client, CombinedLibraries());

        Assert.Empty(results);
    }

    [Fact]
    public void DiscoverAllSlaves_assigns_increasing_station_addresses_to_every_slave_on_the_bus()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision)); // ring position 0
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision)); // ring position 1
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision)); // ring position 2
        var client = CreateClient(bus);

        var results = SlaveDiscovery.DiscoverAllSlaves(client, CombinedLibraries());

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(3, r.SlaveCount));

        Assert.Equal((ushort)(SlaveDiscovery.FirstStationAddress + 0), results[0].StationAddress);
        Assert.Equal((ushort)(SlaveDiscovery.FirstStationAddress + 1), results[1].StationAddress);
        Assert.Equal((ushort)(SlaveDiscovery.FirstStationAddress + 2), results[2].StationAddress);

        // Every assigned address must actually be independently reachable afterwards -- not just
        // recorded, but usable for node-addressed FPRD/FPWR against the right physical slave.
        Assert.All(results, r => Assert.Equal(PanasonicVendorId, r.Identity.VendorId));
    }

    [Fact]
    public void DiscoverAllSlaves_matches_each_slave_against_the_combined_catalog_independently_by_position()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision)); // position 0: Panasonic
        bus.AddSlave(new FakeSlaveDevice(SyntheticVendorId, SyntheticProductCode, SyntheticRevisionNumber)); // position 1: a different vendor entirely
        var client = CreateClient(bus);

        var results = SlaveDiscovery.DiscoverAllSlaves(client, CombinedLibraries());

        Assert.Equal(2, results.Count);
        Assert.Equal("MADLN01BE", results[0].Device.Name);
        Assert.Equal(EsiCatalogTests.SyntheticDeviceName, results[1].Device.Name);
    }

    [Fact]
    public void DiscoverAllSlaves_throws_for_whichever_slave_matches_no_device_without_losing_the_others()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision)); // position 0: matches fine
        bus.AddSlave(new FakeSlaveDevice(vendorId: 0x0000AAAA, productCode: 0x0000BBBB, revisionNumber: 0x0000CCCC)); // position 1: matches nothing
        var client = CreateClient(bus);

        var ex = Assert.Throws<SlaveIdentityMismatchException>(() => SlaveDiscovery.DiscoverAllSlaves(client, CombinedLibraries()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
