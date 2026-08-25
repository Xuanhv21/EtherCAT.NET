using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;

namespace EtherCAT.NET.Engine.Tests.ProcessData;

/// <summary>
/// Builds a <see cref="ProcessImagePlan"/> from the real, embedded Panasonic MINAS A6BE ESI
/// device (MADLN01BE) — not a hand-written fixture — and asserts every computed byte offset, PDO
/// length, and derived FMMU logical range matches the implementation plan's "Byte offset PDO"
/// table exactly.
/// </summary>
public class ProcessImageBuilderTests
{
    private static EsiDeviceDescriptor GetMadln01Be()
    {
        var library = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();
        return library.Devices.Single(d =>
            d.Name == "MADLN01BE" &&
            d.ProductCode == 0x60380000 &&
            d.RevisionNumber == 0x00010000);
    }

    [Fact]
    public void RxPdo_0x1600_layout_matches_the_plan_table_exactly()
    {
        var device = GetMadln01Be();

        var plan = ProcessImageBuilder.BuildDefault(device);
        var rx = plan.RxPdoLayout;

        Assert.Equal(0x1600, rx.PdoIndex);
        Assert.Equal(9, rx.TotalByteLength);
        Assert.Equal(4, rx.Entries.Count);

        AssertEntry(rx.Entries[0], index: 0x6040, subIndex: 0x00, bitLength: 16, name: "Controlword", byteOffset: 0);
        AssertEntry(rx.Entries[1], index: 0x6060, subIndex: 0x00, bitLength: 8, name: "Modes of operation", byteOffset: 2);
        AssertEntry(rx.Entries[2], index: 0x607A, subIndex: 0x00, bitLength: 32, name: "Target position", byteOffset: 3);
        AssertEntry(rx.Entries[3], index: 0x60B8, subIndex: 0x00, bitLength: 16, name: "Touch probe function", byteOffset: 7);
    }

    [Fact]
    public void TxPdo_0x1A00_layout_matches_the_plan_table_exactly()
    {
        var device = GetMadln01Be();

        var plan = ProcessImageBuilder.BuildDefault(device);
        var tx = plan.TxPdoLayout;

        Assert.Equal(0x1A00, tx.PdoIndex);
        Assert.Equal(23, tx.TotalByteLength);
        Assert.Equal(8, tx.Entries.Count);

        AssertEntry(tx.Entries[0], index: 0x603F, subIndex: 0x00, bitLength: 16, name: "Error code", byteOffset: 0);
        AssertEntry(tx.Entries[1], index: 0x6041, subIndex: 0x00, bitLength: 16, name: "Statusword", byteOffset: 2);
        AssertEntry(tx.Entries[2], index: 0x6061, subIndex: 0x00, bitLength: 8, name: "Modes of operation display", byteOffset: 4);
        AssertEntry(tx.Entries[3], index: 0x6064, subIndex: 0x00, bitLength: 32, name: "Position actual value", byteOffset: 5);
        AssertEntry(tx.Entries[4], index: 0x60B9, subIndex: 0x00, bitLength: 16, name: "Touch probe status", byteOffset: 9);
        AssertEntry(tx.Entries[5], index: 0x60BA, subIndex: 0x00, bitLength: 32, name: "Touch probe pos1 pos value", byteOffset: 11);
        AssertEntry(tx.Entries[6], index: 0x60F4, subIndex: 0x00, bitLength: 32, name: "Following error actual value", byteOffset: 15);
        AssertEntry(tx.Entries[7], index: 0x60FD, subIndex: 0x00, bitLength: 32, name: "Digital inputs", byteOffset: 19);
    }

    [Fact]
    public void Fmmu0_outputs_logical_range_is_0_to_9_write_only_at_sm2_physical_address()
    {
        var device = GetMadln01Be();

        var plan = ProcessImageBuilder.BuildDefault(device);
        var fmmu0 = plan.OutputsFmmu;

        Assert.Equal(0u, fmmu0.LogicalStartAddress);
        Assert.Equal(9, fmmu0.Length);
        Assert.Equal(9u, fmmu0.LogicalStartAddress + fmmu0.Length); // [0, 9)

        Assert.Equal(0x1400, fmmu0.PhysicalStartAddress); // SM2 Outputs StartAddress, verbatim from ESI.
        Assert.False(fmmu0.ReadEnabled);
        Assert.True(fmmu0.WriteEnabled);
        Assert.True(fmmu0.Enable);
    }

    [Fact]
    public void Fmmu1_inputs_logical_range_is_9_to_32_read_only_at_sm3_physical_address()
    {
        var device = GetMadln01Be();

        var plan = ProcessImageBuilder.BuildDefault(device);
        var fmmu1 = plan.InputsFmmu;

        Assert.Equal(9u, fmmu1.LogicalStartAddress);
        Assert.Equal(23, fmmu1.Length);
        Assert.Equal(32u, fmmu1.LogicalStartAddress + fmmu1.Length); // [9, 32)

        Assert.Equal(0x1600, fmmu1.PhysicalStartAddress); // SM3 Inputs StartAddress, verbatim from ESI.
        Assert.True(fmmu1.ReadEnabled);
        Assert.False(fmmu1.WriteEnabled);
        Assert.True(fmmu1.Enable);
    }

    private static void AssertEntry(PdoEntryDescriptor entry, ushort index, byte subIndex, byte bitLength, string name, int byteOffset)
    {
        Assert.Equal(index, entry.Index);
        Assert.Equal(subIndex, entry.SubIndex);
        Assert.Equal(bitLength, entry.BitLength);
        Assert.Equal(name, entry.Name);
        Assert.Equal(byteOffset, entry.ByteOffset);
    }

    // --- BuildMulti: combined outputs-then-inputs layout across a GROUP of slaves. ---

    [Fact]
    public void BuildMulti_throws_for_an_empty_slave_list()
    {
        Assert.Throws<ArgumentException>(() => ProcessImageBuilder.BuildMulti([]));
    }

    [Fact]
    public void BuildMulti_lays_out_two_slaves_outputs_then_inputs_with_correct_offsets_and_expected_wkc()
    {
        var device = GetMadln01Be();
        const ushort station0 = 0x1001;
        const ushort station1 = 0x1002;

        var plan = ProcessImageBuilder.BuildMulti([(station0, device), (station1, device)]);

        // 9-byte outputs + 23-byte inputs per slave (the same single-slave lengths asserted above),
        // concatenated: slave0 outputs [0,9), slave1 outputs [9,18); total outputs = 18.
        Assert.Equal(18, plan.TotalOutputsLength);
        Assert.Equal(46, plan.TotalInputsLength); // 23 + 23
        Assert.Equal(64, plan.TotalLength);
        Assert.Equal((ushort)4, plan.ExpectedWorkingCounter); // 2 per slave x 2 slaves.

        Assert.Equal(2, plan.Slaves.Count);

        var slave0 = plan.Slaves[0];
        Assert.Equal(station0, slave0.StationAddress);
        Assert.Equal(0, slave0.OutputsOffset);
        Assert.Equal(0, slave0.InputsOffset);
        Assert.Equal(0u, slave0.OutputsFmmu.LogicalStartAddress);
        Assert.Equal(9, slave0.OutputsFmmu.Length);
        Assert.Equal(0x1400, slave0.OutputsFmmu.PhysicalStartAddress);
        Assert.True(slave0.OutputsFmmu.WriteEnabled);
        Assert.False(slave0.OutputsFmmu.ReadEnabled);
        // Slave 0's inputs sit right at the start of the combined inputs region (offset 18 overall).
        Assert.Equal((uint)plan.TotalOutputsLength, slave0.InputsFmmu.LogicalStartAddress);
        Assert.Equal(23, slave0.InputsFmmu.Length);
        Assert.Equal(0x1600, slave0.InputsFmmu.PhysicalStartAddress);
        Assert.True(slave0.InputsFmmu.ReadEnabled);
        Assert.False(slave0.InputsFmmu.WriteEnabled);

        var slave1 = plan.Slaves[1];
        Assert.Equal(station1, slave1.StationAddress);
        Assert.Equal(9, slave1.OutputsOffset); // right after slave0's 9 bytes.
        Assert.Equal(23, slave1.InputsOffset); // right after slave0's 23 bytes, within the inputs region.
        Assert.Equal(9u, slave1.OutputsFmmu.LogicalStartAddress);
        Assert.Equal(9, slave1.OutputsFmmu.Length);
        // Slave 1's inputs sit right after slave 0's inputs within the combined inputs region: 18 + 23 = 41.
        Assert.Equal((uint)(plan.TotalOutputsLength + 23), slave1.InputsFmmu.LogicalStartAddress);
        Assert.Equal(23, slave1.InputsFmmu.Length);

        // Physical addresses are per-slave-local (each slave's own SM2/SM3), so both slaves' FMMUs
        // point at the same physical addresses even though their logical ranges never overlap.
        Assert.Equal(slave0.OutputsFmmu.PhysicalStartAddress, slave1.OutputsFmmu.PhysicalStartAddress);
        Assert.Equal(slave0.InputsFmmu.PhysicalStartAddress, slave1.InputsFmmu.PhysicalStartAddress);
    }

    [Fact]
    public void BuildMulti_with_one_slave_matches_BuildDefault_single_slave_layout()
    {
        var device = GetMadln01Be();

        var multi = ProcessImageBuilder.BuildMulti([((ushort)0x1001, device)]);
        var single = ProcessImageBuilder.BuildDefault(device);

        Assert.Single(multi.Slaves);
        Assert.Equal(single.RxPdoLayout.TotalByteLength, multi.TotalOutputsLength);
        Assert.Equal(single.TxPdoLayout.TotalByteLength, multi.TotalInputsLength);
        Assert.Equal((ushort)2, multi.ExpectedWorkingCounter);
        Assert.Equal(single.OutputsFmmu, multi.Slaves[0].OutputsFmmu);
        Assert.Equal(single.InputsFmmu, multi.Slaves[0].InputsFmmu);
    }
}
