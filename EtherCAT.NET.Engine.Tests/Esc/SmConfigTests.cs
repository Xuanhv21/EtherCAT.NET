using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.Tests.Esc;

/// <summary>
/// <see cref="SmConfig"/>.ToBytes()/FromBytes() against the exact 8-byte layout and the four Sync
/// Manager rows tabulated in the plan (verbatim ControlByte from the ESI, never recomputed from
/// sub-bits): SM0 MBoxOut 0x1000/256/0x26, SM1 MBoxIn 0x1200/256/0x22, SM2 Outputs 0x1400/9/0x64,
/// SM3 Inputs 0x1600/23/0x20.
/// </summary>
public class SmConfigTests
{
    public static readonly TheoryData<ushort, ushort, byte> PlanSyncManagers = new()
    {
        { 0x1000, 256, 0x26 }, // SM0 MBoxOut
        { 0x1200, 256, 0x22 }, // SM1 MBoxIn
        { 0x1400, 9, 0x64 },   // SM2 Outputs
        { 0x1600, 23, 0x20 },  // SM3 Inputs
    };

    [Theory]
    [MemberData(nameof(PlanSyncManagers))]
    public void ToBytes_emits_the_exact_wire_layout_for_each_plan_Sync_Manager(ushort physicalStartAddress, ushort length, byte controlByte)
    {
        var sm = new SmConfig(physicalStartAddress, length, controlByte);

        var expected = new byte[]
        {
            (byte)physicalStartAddress, (byte)(physicalStartAddress >> 8),
            (byte)length, (byte)(length >> 8),
            controlByte,
            0x00, // Status (read-only, always 0 on write)
            0x01, // Activate
            0x00, // PDI Control
        };

        Assert.Equal(SmConfig.ByteLength, expected.Length);
        Assert.Equal(expected, sm.ToBytes());
    }

    [Theory]
    [MemberData(nameof(PlanSyncManagers))]
    public void FromBytes_is_the_exact_inverse_of_ToBytes(ushort physicalStartAddress, ushort length, byte controlByte)
    {
        var original = new SmConfig(physicalStartAddress, length, controlByte);

        var roundTripped = SmConfig.FromBytes(original.ToBytes());

        Assert.Equal(original, roundTripped);
        Assert.Equal(controlByte, roundTripped.ControlByte); // verbatim, not recomputed from sub-bits.
    }

    [Fact]
    public void The_ControlByte_is_stored_and_emitted_verbatim_even_for_an_unrecognised_bit_pattern()
    {
        // 0xFF does not correspond to any "sensible" decomposition of buffer mode / direction /
        // interrupt sub-bits -- the point of storing it verbatim is that SmConfig must not care.
        var sm = new SmConfig(0x1400, 9, ControlByte: 0xFF);

        Assert.Equal(0xFF, sm.ToBytes()[4]);
        Assert.Equal((byte)0xFF, SmConfig.FromBytes(sm.ToBytes()).ControlByte);
    }

    [Fact]
    public void An_inactive_Sync_Manager_round_trips_Enable_false()
    {
        var original = new SmConfig(0x1000, 256, 0x26, Enable: false);

        var bytes = original.ToBytes();
        Assert.Equal(0x00, bytes[6]);

        Assert.False(SmConfig.FromBytes(bytes).Enable);
    }

    [Fact]
    public void ToBytes_rejects_a_destination_shorter_than_8_bytes()
    {
        var sm = new SmConfig(0x1000, 256, 0x26);
        Assert.Throws<ArgumentException>(() => sm.WriteTo(new byte[7]));
    }

    [Fact]
    public void FromBytes_rejects_a_source_shorter_than_8_bytes()
    {
        Assert.Throws<ArgumentException>(() => SmConfig.FromBytes(new byte[7]));
    }
}
