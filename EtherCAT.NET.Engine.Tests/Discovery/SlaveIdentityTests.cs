using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.Discovery;

/// <summary>
/// <see cref="SlaveIdentity.Read"/> exercised against <see cref="FakeBus"/>/<see cref="FakeSlaveDevice"/>:
/// the values it returns are exactly what was seeded into the fake slave's SII EEPROM, obtained
/// through the real register-mapped <see cref="SiiEeprom"/>/<see cref="EscClient"/> path (FPRD/FPWR
/// against 0x0502/0x0504/0x0508) rather than by reading the fake's backing storage directly.
/// </summary>
public class SlaveIdentityTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    [Fact]
    public void Read_returns_the_seeded_Panasonic_vendor_product_and_revision()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision));
        var client = new EscClient(new FakeEthernetFrameTransport(bus), TestSourceMac);
        SlaveDiscovery.AssignStationAddress(client, SlaveDiscovery.FirstStationAddress);

        var identity = SlaveIdentity.Read(client, SlaveDiscovery.FirstStationAddress);

        Assert.Equal(PanasonicVendorId, identity.VendorId);
        Assert.Equal(Madln01BeProductCode, identity.ProductCode);
        Assert.Equal(Madln01BeRevision, identity.RevisionNumber);
    }

    [Fact]
    public void Read_reflects_whatever_identity_was_seeded_not_a_fixed_value()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice(vendorId: 0x00000042, productCode: 0x00030924, revisionNumber: 0x00020001));
        var client = new EscClient(new FakeEthernetFrameTransport(bus), TestSourceMac);
        SlaveDiscovery.AssignStationAddress(client, SlaveDiscovery.FirstStationAddress);

        var identity = SlaveIdentity.Read(client, SlaveDiscovery.FirstStationAddress);

        Assert.Equal(new SlaveIdentity(0x00000042, 0x00030924, 0x00020001), identity);
    }
}
