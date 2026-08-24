using System.Buffers.Binary;
using EtherCAT.NET.Engine.Protocol;

namespace EtherCAT.NET.Engine.Tests.Fakes;

/// <summary>
/// Runs hand-built EtherCAT frames through <see cref="FakeBus"/> and checks the reply frame it
/// hand-encodes (parsed back with the real <see cref="EtherCatFrameParser"/>) matches what a real
/// bus would produce: BRD counting slaves, a register round trip via FPWR/FPRD, LRW resolving
/// through FMMU/SM configuration, and the failure-injection hooks later tests will rely on.
/// </summary>
public class FakeBusTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    [Fact]
    public void Brd_against_a_bus_with_one_registered_slave_returns_Wkc_1()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());

        var datagram = new EtherCatDatagram(
            EtherCatCommand.Brd,
            index: 0x01,
            EtherCatAddress.ForNodeAddressed(adp: 0x0000, ado: 0x0000),
            data: [0x00, 0x00]);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        var reply = bus.Process(frame);
        Assert.NotNull(reply);

        var parsed = EtherCatFrameParser.Parse(reply);
        Assert.Single(parsed.Datagrams);
        Assert.Equal((ushort)1, parsed.Datagrams[0].WorkingCounter);
    }

    [Fact]
    public void Brd_Wkc_counts_every_registered_slave()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: [0x00, 0x00]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        var parsed = EtherCatFrameParser.Parse(bus.Process(frame)!);

        Assert.Equal((ushort)3, parsed.Datagrams[0].WorkingCounter);
    }

    [Fact]
    public void Fpwr_then_Fprd_on_the_same_register_round_trips_the_written_bytes()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice()); // ConfiguredStationAddress defaults to 0x0000.

        const ushort testRegister = 0x0900; // scratch register, outside the AL/FMMU/SM special ranges.

        var writeDatagram = new EtherCatDatagram(
            EtherCatCommand.Fpwr,
            index: 0x01,
            EtherCatAddress.ForNodeAddressed(adp: 0x0000, ado: testRegister),
            data: [0xDE, 0xAD, 0xBE, 0xEF]);

        var writeFrame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [writeDatagram]);
        var writeReply = EtherCatFrameParser.Parse(bus.Process(writeFrame)!);
        Assert.Equal((ushort)1, writeReply.Datagrams[0].WorkingCounter);

        var readDatagram = new EtherCatDatagram(
            EtherCatCommand.Fprd,
            index: 0x02,
            EtherCatAddress.ForNodeAddressed(adp: 0x0000, ado: testRegister),
            data: new byte[4]);

        var readFrame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [readDatagram]);
        var readReply = EtherCatFrameParser.Parse(bus.Process(readFrame)!);

        Assert.Equal((ushort)1, readReply.Datagrams[0].WorkingCounter);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, readReply.Datagrams[0].Data);
    }

    [Fact]
    public void Fprd_against_an_unmatched_Configured_Station_Address_returns_Wkc_0()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice()); // Configured Station Address 0x0000.

        var datagram = new EtherCatDatagram(
            EtherCatCommand.Fprd,
            index: 0x01,
            EtherCatAddress.ForNodeAddressed(adp: 0x0007, ado: 0x0900), // no slave has this address
            data: new byte[2]);

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);
        var parsed = EtherCatFrameParser.Parse(bus.Process(frame)!);

        Assert.Equal((ushort)0, parsed.Datagrams[0].WorkingCounter);
    }

    [Fact]
    public void Apwr_to_register_0x0010_assigns_the_Configured_Station_Address_of_the_slave_at_that_ring_position()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());

        var datagram = new EtherCatDatagram(
            EtherCatCommand.Apwr,
            index: 0x01,
            EtherCatAddress.ForNodeAddressed(adp: 0x0000, ado: FakeSlaveDevice.ConfiguredStationAddressRegister),
            data: [0x01, 0x00]); // Configured Station Address = 1

        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);
        var parsed = EtherCatFrameParser.Parse(bus.Process(frame)!);

        Assert.Equal((ushort)1, parsed.Datagrams[0].WorkingCounter);
        Assert.Equal((ushort)0x0001, bus.Slaves[0].ConfiguredStationAddress);
    }

    [Fact]
    public void Lrw_round_trips_process_data_through_configured_FMMUs_and_accumulates_Wkc_2()
    {
        var slave = new FakeSlaveDevice();

        WriteFmmu(slave, index: 0, logicalStart: 0x00000000, length: 9, physicalStart: 0x1400, read: false, write: true);
        WriteFmmu(slave, index: 1, logicalStart: 0x00000009, length: 23, physicalStart: 0x1600, read: true, write: false);

        var txPdo = new byte[23];
        for (var i = 0; i < txPdo.Length; i++)
        {
            txPdo[i] = (byte)(0x40 + i);
        }

        slave.WriteRegisterBytes(0x1600, txPdo);

        var bus = new FakeBus();
        bus.AddSlave(slave);

        var rxPdo = new byte[9];
        for (var i = 0; i < rxPdo.Length; i++)
        {
            rxPdo[i] = (byte)(0x10 + i);
        }

        var lrwData = new byte[32];
        rxPdo.CopyTo(lrwData, 0); // input region (bytes 9..31) left zero, as a real master would send it.

        var datagram = new EtherCatDatagram(EtherCatCommand.Lrw, index: 0x01, EtherCatAddress.ForLogicalAddressed(0), lrwData);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        var parsed = EtherCatFrameParser.Parse(bus.Process(frame)!);

        Assert.Equal((ushort)2, parsed.Datagrams[0].WorkingCounter); // +1 write FMMU, +1 read FMMU
        Assert.Equal(rxPdo, parsed.Datagrams[0].Data[..9]);
        Assert.Equal(txPdo, parsed.Datagrams[0].Data[9..]);
        Assert.Equal(rxPdo, slave.ReadRegisterBytes(0x1400, 9)); // output landed in the slave's physical SM2 memory
    }

    [Fact]
    public void DropAllFrames_causes_Process_to_return_null()
    {
        var bus = new FakeBus { DropAllFrames = true };
        bus.AddSlave(new FakeSlaveDevice());

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: new byte[2]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        Assert.Null(bus.Process(frame));
    }

    [Fact]
    public void WorkingCounterOverride_can_force_a_wrong_Wkc()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());
        bus.WorkingCounterOverride = (_, _) => 99;

        var datagram = new EtherCatDatagram(EtherCatCommand.Brd, 0x01, EtherCatAddress.ForNodeAddressed(0, 0), data: new byte[2]);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, TestSourceMac, [datagram]);

        var parsed = EtherCatFrameParser.Parse(bus.Process(frame)!);
        Assert.Equal((ushort)99, parsed.Datagrams[0].WorkingCounter);
    }

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
