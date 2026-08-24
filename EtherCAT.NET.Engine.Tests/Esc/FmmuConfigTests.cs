using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.Tests.Esc;

/// <summary>
/// <see cref="FmmuConfig"/>.ToBytes()/FromBytes() against the exact 16-byte layout the plan
/// requires: Logical Start Address(4) | Length(2) | LogicalStartBit(1) | LogicalStopBit(1) |
/// Physical Start Address(2) | PhysicalStartBit(1) | Reserved(1) | Type(1) | Activate(1) | Reserved(2).
/// </summary>
public class FmmuConfigTests
{
    [Fact]
    public void ToBytes_produces_the_exact_wire_layout_for_FMMU0_outputs()
    {
        // FMMU0 (Outputs): Logical 0x00000000, Len=9, Phys=0x1400, Write-only, Active.
        var fmmu = FmmuConfig.ForByteAlignedRegion(
            logicalStartAddress: 0x00000000,
            length: 9,
            physicalStartAddress: 0x1400,
            readEnabled: false,
            writeEnabled: true);

        var expected = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // Logical Start Address
            0x09, 0x00,             // Length
            0x00,                   // Logical Start Bit
            0x07,                   // Logical Stop Bit
            0x00, 0x14,             // Physical Start Address
            0x00,                   // Physical Start Bit
            0x00,                   // Reserved
            0x02,                   // Type: write-only
            0x01,                   // Activate
            0x00, 0x00,             // Reserved
        };

        Assert.Equal(FmmuConfig.ByteLength, expected.Length);
        Assert.Equal(expected, fmmu.ToBytes());
    }

    [Fact]
    public void ToBytes_produces_the_exact_wire_layout_for_FMMU1_inputs()
    {
        // FMMU1 (Inputs): Logical 0x00000009, Len=23, Phys=0x1600, Read-only, Active.
        var fmmu = FmmuConfig.ForByteAlignedRegion(
            logicalStartAddress: 0x00000009,
            length: 23,
            physicalStartAddress: 0x1600,
            readEnabled: true,
            writeEnabled: false);

        var expected = new byte[]
        {
            0x09, 0x00, 0x00, 0x00,
            0x17, 0x00,
            0x00,
            0x07,
            0x00, 0x16,
            0x00,
            0x00,
            0x01, // Type: read-only
            0x01,
            0x00, 0x00,
        };

        Assert.Equal(expected, fmmu.ToBytes());
    }

    [Fact]
    public void FromBytes_is_the_exact_inverse_of_ToBytes_for_FMMU0_outputs()
    {
        var original = FmmuConfig.ForByteAlignedRegion(0x00000000, 9, 0x1400, readEnabled: false, writeEnabled: true);

        var roundTripped = FmmuConfig.FromBytes(original.ToBytes());

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void FromBytes_is_the_exact_inverse_of_ToBytes_for_FMMU1_inputs()
    {
        var original = FmmuConfig.ForByteAlignedRegion(0x00000009, 23, 0x1600, readEnabled: true, writeEnabled: false);

        var roundTripped = FmmuConfig.FromBytes(original.ToBytes());

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void An_inactive_FMMU_round_trips_Enable_false()
    {
        var original = new FmmuConfig(0, 0, 0, 0, 0, 0, ReadEnabled: false, WriteEnabled: false, Enable: false);

        var bytes = original.ToBytes();
        Assert.Equal(0x00, bytes[13]); // Activate byte clear.

        var roundTripped = FmmuConfig.FromBytes(bytes);
        Assert.False(roundTripped.Enable);
    }

    [Fact]
    public void ToBytes_rejects_a_destination_shorter_than_16_bytes()
    {
        var fmmu = FmmuConfig.ForByteAlignedRegion(0, 9, 0x1400, false, true);
        Assert.Throws<ArgumentException>(() => fmmu.WriteTo(new byte[15]));
    }

    [Fact]
    public void FromBytes_rejects_a_source_shorter_than_16_bytes()
    {
        Assert.Throws<ArgumentException>(() => FmmuConfig.FromBytes(new byte[15]));
    }
}
