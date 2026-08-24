using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Tests.Fakes;

/// <summary>
/// Exercises <see cref="FakeSlaveDevice"/> in isolation (no <see cref="FakeBus"/> involved): the SII
/// identity seeding, the AL Control -> AL Status/AL Status Code reaction and its refusal hook, and
/// the FMMU-driven logical-address resolution that later PDO exchange tests will rely on.
/// </summary>
public class FakeSlaveDeviceTests
{
    [Fact]
    public void Boots_in_AL_state_INIT_with_no_AL_error()
    {
        var slave = new FakeSlaveDevice();

        Assert.Equal((ushort)0x0001, slave.AlStatus);
        Assert.Equal((ushort)0x0000, slave.AlStatusCode);
    }

    [Fact]
    public void SeedSiiIdentity_writes_Vendor_Product_Revision_at_the_standard_word_offsets()
    {
        var slave = new FakeSlaveDevice(vendorId: 0x00000002, productCode: 0x00030924, revisionNumber: 0x00010000);

        Assert.Equal(0x00000002u, ReadSiiUInt32(slave, FakeSlaveDevice.SiiVendorIdWordOffset));
        Assert.Equal(0x00030924u, ReadSiiUInt32(slave, FakeSlaveDevice.SiiProductCodeWordOffset));
        Assert.Equal(0x00010000u, ReadSiiUInt32(slave, FakeSlaveDevice.SiiRevisionWordOffset));
    }

    [Fact]
    public void Writing_AL_Control_updates_AL_Status_when_no_refusal_hook_is_set()
    {
        var slave = new FakeSlaveDevice();

        slave.WriteRegisterBytes(FakeSlaveDevice.AlControlRegister, [0x02, 0x00]); // request PREOP

        Assert.Equal((ushort)0x0002, slave.AlStatus);
        Assert.Equal((ushort)0x0000, slave.AlStatusCode);
    }

    [Fact]
    public void TransitionRefusal_hook_can_force_a_specific_AL_Status_Code_instead_of_transitioning()
    {
        var slave = new FakeSlaveDevice();
        slave.TransitionRefusal = (_, requested) => requested == 0x0002 ? (ushort)0x0011 : null;

        slave.WriteRegisterBytes(FakeSlaveDevice.AlControlRegister, [0x02, 0x00]); // request PREOP, refused

        Assert.Equal((ushort)(0x0001 | 0x0010), slave.AlStatus); // still INIT, Error flag set
        Assert.Equal((ushort)0x0011, slave.AlStatusCode);
    }

    [Fact]
    public void Register_writes_outside_AL_Control_are_stored_verbatim_with_no_side_effects()
    {
        var slave = new FakeSlaveDevice();
        var fmmuBlock = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        slave.WriteRegisterBytes(FakeSlaveDevice.FmmuBaseRegister, fmmuBlock);

        Assert.Equal(fmmuBlock, slave.ReadRegisterBytes(FakeSlaveDevice.FmmuBaseRegister, 16));
        Assert.Equal((ushort)0x0001, slave.AlStatus); // untouched
    }

    [Fact]
    public void Lrw_style_read_and_write_resolve_through_configured_FMMUs_to_the_right_physical_range()
    {
        var slave = new FakeSlaveDevice();

        // FMMU0 (Outputs): Logical 0x00000000, Len=9, Phys=0x1400, write-only.
        WriteFmmu(slave, index: 0, logicalStart: 0x00000000, length: 9, physicalStart: 0x1400, read: false, write: true);
        // FMMU1 (Inputs): Logical 0x00000009, Len=23, Phys=0x1600, read-only.
        WriteFmmu(slave, index: 1, logicalStart: 0x00000009, length: 23, physicalStart: 0x1600, read: true, write: false);

        var txPdo = new byte[23];
        for (var i = 0; i < txPdo.Length; i++)
        {
            txPdo[i] = (byte)(0x40 + i);
        }

        slave.WriteRegisterBytes(0x1600, txPdo);

        var rxPdo = new byte[9];
        for (var i = 0; i < rxPdo.Length; i++)
        {
            rxPdo[i] = (byte)(0x10 + i);
        }

        var frameData = new byte[32];
        rxPdo.CopyTo(frameData, 0);

        var wroteAny = slave.TryApplyLogicalWrite(0, frameData);
        Assert.True(wroteAny);
        Assert.Equal(rxPdo, slave.ReadRegisterBytes(0x1400, 9));

        var readAny = slave.TryApplyLogicalRead(0, frameData);
        Assert.True(readAny);
        Assert.Equal(rxPdo, frameData[..9]);
        Assert.Equal(txPdo, frameData[9..]);
    }

    [Fact]
    public void FMMU_resolution_ignores_ranges_outside_its_own_logical_window()
    {
        var slave = new FakeSlaveDevice();
        WriteFmmu(slave, index: 0, logicalStart: 100, length: 5, physicalStart: 0x1400, read: true, write: true);

        var buffer = new byte[5];
        var processed = slave.TryApplyLogicalRead(logicalAddress: 0, buffer);

        Assert.False(processed);
    }

    private static uint ReadSiiUInt32(FakeSlaveDevice slave, ushort wordOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(slave.ReadSiiBytes((ushort)(wordOffset * 2), 4));

    private static void WriteFmmu(FakeSlaveDevice slave, int index, uint logicalStart, ushort length, ushort physicalStart, bool read, bool write)
    {
        var block = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), logicalStart);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4, 2), length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), physicalStart);

        byte type = 0;
        if (read)
        {
            type |= 0x01;
        }

        if (write)
        {
            type |= 0x02;
        }

        block[12] = type;
        block[13] = 0x01; // Activate

        var address = (ushort)(FakeSlaveDevice.FmmuBaseRegister + (index * FakeSlaveDevice.FmmuStride));
        slave.WriteRegisterBytes(address, block);
    }
}
