using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Tests.Esi;

/// <summary>
/// Parses the real, embedded Panasonic MINAS A6BE ESI file (not a hand-written fixture) and
/// asserts the resulting <see cref="EsiDeviceLibrary"/> matches values read directly from that
/// file, so a change in parsing logic that silently breaks a real field gets caught here.
/// </summary>
public class EsiXmlParserTests
{
    private const string ExpectedDeviceName = "MADLN01BE";
    private const uint ExpectedProductCode = 0x60380000;
    private const uint ExpectedRevisionNumber = 0x00010000;

    private static EsiDeviceLibrary ParseEmbeddedLibrary() => EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();

    private static EsiDeviceDescriptor GetMadln01Be(EsiDeviceLibrary library) =>
        library.Devices.Single(d =>
            d.Name == ExpectedDeviceName &&
            d.ProductCode == ExpectedProductCode &&
            d.RevisionNumber == ExpectedRevisionNumber);

    [Fact]
    public void Parses_vendor_id_and_name()
    {
        var library = ParseEmbeddedLibrary();

        Assert.Equal(0x066Fu, library.Vendor.Id);
        Assert.False(string.IsNullOrWhiteSpace(library.Vendor.Name));
    }

    [Fact]
    public void Parses_many_repeated_device_blocks_not_just_one()
    {
        var library = ParseEmbeddedLibrary();

        // The real file repeats the same Sm/Fmmu/RxPdo/TxPdo/Mailbox/Dc structure across many
        // <Device> blocks for different Panasonic wattage variants — all must land in Devices.
        Assert.True(library.Devices.Count > 1, "Expected more than one parsed <Device>.");
    }

    [Fact]
    public void Finds_device_MADLN01BE_with_expected_product_code_and_revision()
    {
        var library = ParseEmbeddedLibrary();

        var device = library.Devices.SingleOrDefault(d => d.Name == ExpectedDeviceName);

        Assert.NotNull(device);
        Assert.Equal(ExpectedProductCode, device!.ProductCode);
        Assert.Equal(ExpectedRevisionNumber, device.RevisionNumber);
    }

    [Fact]
    public void MADLN01BE_has_exactly_4_sync_managers_with_expected_addresses_control_bytes_and_sizes()
    {
        var library = ParseEmbeddedLibrary();
        var device = GetMadln01Be(library);

        Assert.Equal(4, device.SyncManagers.Count);

        AssertSyncManager(device.SyncManagers[0], startAddress: 0x1000, controlByte: 0x26, defaultSize: 256);
        AssertSyncManager(device.SyncManagers[1], startAddress: 0x1200, controlByte: 0x22, defaultSize: 256);
        AssertSyncManager(device.SyncManagers[2], startAddress: 0x1400, controlByte: 0x64, defaultSize: 9);
        AssertSyncManager(device.SyncManagers[3], startAddress: 0x1600, controlByte: 0x20, defaultSize: 23);
    }

    private static void AssertSyncManager(EsiSyncManager sm, ushort startAddress, byte controlByte, ushort defaultSize)
    {
        Assert.Equal(startAddress, sm.StartAddress);
        Assert.Equal(controlByte, sm.ControlByte);
        Assert.Equal(defaultSize, sm.DefaultSize);
    }

    [Fact]
    public void MADLN01BE_RxPdo_0x1600_has_4_entries_matching_the_CiA402_control_mapping()
    {
        var library = ParseEmbeddedLibrary();
        var device = GetMadln01Be(library);

        var rxPdo = device.RxPdos.Single(p => p.Index == 0x1600);

        Assert.Equal(4, rxPdo.Entries.Count);

        AssertEntry(rxPdo.Entries[0], index: 0x6040, subIndex: 0x00, bitLength: 16, name: "Controlword");
        AssertEntry(rxPdo.Entries[1], index: 0x6060, subIndex: 0x00, bitLength: 8, name: "Modes of operation");
        AssertEntry(rxPdo.Entries[2], index: 0x607A, subIndex: 0x00, bitLength: 32, name: "Target position");
        AssertEntry(rxPdo.Entries[3], index: 0x60B8, subIndex: 0x00, bitLength: 16, name: "Touch probe function");
    }

    [Fact]
    public void MADLN01BE_TxPdo_0x1A00_has_8_entries_starting_with_error_code_and_including_statusword()
    {
        var library = ParseEmbeddedLibrary();
        var device = GetMadln01Be(library);

        var txPdo = device.TxPdos.Single(p => p.Index == 0x1A00);

        Assert.Equal(8, txPdo.Entries.Count);

        AssertEntry(txPdo.Entries[0], index: 0x603F, subIndex: 0x00, bitLength: 16, name: "Error code");
        AssertEntry(txPdo.Entries[1], index: 0x6041, subIndex: 0x00, bitLength: 16, name: "Statusword");

        Assert.Contains(
            txPdo.Entries,
            e => e.Index == 0x6041 && e.SubIndex == 0x00 && e.BitLength == 16 && e.Name == "Statusword");
    }

    private static void AssertEntry(EsiPdoEntry entry, ushort index, byte subIndex, int bitLength, string name)
    {
        Assert.Equal(index, entry.Index);
        Assert.Equal(subIndex, entry.SubIndex);
        Assert.Equal(bitLength, entry.BitLength);
        Assert.Equal(name, entry.Name);
    }
}
