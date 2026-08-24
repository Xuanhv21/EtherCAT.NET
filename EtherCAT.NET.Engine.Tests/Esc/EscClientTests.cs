using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.Esc;

/// <summary>
/// <see cref="EscClient"/> exercised against <see cref="FakeBus"/>/<see cref="FakeSlaveDevice"/>/
/// <see cref="FakeEthernetFrameTransport"/>: plain register read/write round trips, the AL Control
/// write -> AL Status read path (including the injected-failure/AL Status Code path), FMMU/SM
/// config helpers, and WKC-mismatch error handling.
/// </summary>
public class EscClientTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const ushort StationAddress = 0x1001;

    private static (EscClient Client, FakeBus Bus, FakeSlaveDevice Slave) CreateClientWithOneConfiguredSlave()
    {
        var slave = new FakeSlaveDevice();
        var bus = new FakeBus();
        bus.AddSlave(slave);

        var transport = new FakeEthernetFrameTransport(bus);
        var client = new EscClient(transport, TestSourceMac);

        // Auto-increment position 0 (the first/only slave) -> assign the Configured Station Address.
        client.ConfigureStationAddress(autoIncrementAddress: 0x0000, StationAddress);

        return (client, bus, slave);
    }

    [Fact]
    public void WriteRegister_then_ReadRegister_round_trips_arbitrary_bytes_through_the_fake_bus()
    {
        var (client, _, _) = CreateClientWithOneConfiguredSlave();
        const ushort scratchRegister = 0x0900; // outside the AL/FMMU/SM special ranges.

        client.WriteRegister(StationAddress, scratchRegister, [0xDE, 0xAD, 0xBE, 0xEF]);
        var readBack = client.ReadRegister(StationAddress, scratchRegister, 4);

        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, readBack);
    }

    [Fact]
    public void ConfigureStationAddress_assigns_the_address_the_fake_slave_reports()
    {
        var (_, _, slave) = CreateClientWithOneConfiguredSlave();

        Assert.Equal(StationAddress, slave.ConfiguredStationAddress);
    }

    [Fact]
    public void ReadRegister_throws_EscWorkingCounterException_when_no_slave_matches_the_station_address()
    {
        var slave = new FakeSlaveDevice();
        var bus = new FakeBus();
        bus.AddSlave(slave); // left at its default Configured Station Address (0x0000).

        var client = new EscClient(new FakeEthernetFrameTransport(bus), TestSourceMac);

        var ex = Assert.Throws<EscWorkingCounterException>(() => client.ReadRegister(0x1234, 0x0900, 2));

        Assert.Equal(EtherCatCommand.Fprd, ex.Command);
        Assert.Equal((ushort)1, ex.ExpectedWorkingCounter);
        Assert.Equal((ushort)0, ex.ActualWorkingCounter);
    }

    [Fact]
    public void WriteRegister_throws_EscWorkingCounterException_when_the_bus_overrides_the_Wkc()
    {
        var (client, bus, _) = CreateClientWithOneConfiguredSlave();
        bus.WorkingCounterOverride = (_, _) => 0;

        var ex = Assert.Throws<EscWorkingCounterException>(() => client.WriteRegister(StationAddress, 0x0900, [0x01, 0x02]));

        Assert.Equal(EtherCatCommand.Fpwr, ex.Command);
        Assert.Equal((ushort)1, ex.ExpectedWorkingCounter);
        Assert.Equal((ushort)0, ex.ActualWorkingCounter);
    }

    [Fact]
    public void BroadcastRead_returns_the_Wkc_as_the_slave_count_without_throwing()
    {
        var bus = new FakeBus();
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());
        bus.AddSlave(new FakeSlaveDevice());

        var client = new EscClient(new FakeEthernetFrameTransport(bus), TestSourceMac);

        var (_, slaveCount) = client.BroadcastRead(EscRegisters.ConfiguredStationAddressRegister, 2);

        Assert.Equal((ushort)3, slaveCount);
    }

    [Fact]
    public void WriteAlControl_then_ReadAlStatus_reaches_the_requested_state_when_the_fake_slave_accepts_the_transition()
    {
        var (client, _, _) = CreateClientWithOneConfiguredSlave();

        client.WriteAlControl(StationAddress, AlState.PreOp);
        var status = client.ReadAlStatus(StationAddress);

        Assert.Equal(AlState.PreOp, status.State);
        Assert.False(status.HasError);
        Assert.Equal(AlStatusCode.NoError, status.StatusCode);
    }

    [Fact]
    public void WriteAlControl_surfaces_the_injected_refusal_and_AL_Status_Code_instead_of_the_requested_state()
    {
        var (client, _, slave) = CreateClientWithOneConfiguredSlave();

        // Slave starts in Init (0x0001). Get it to PreOp normally first, then force the PreOp -> SafeOp
        // transition to be refused with a specific AL Status Code, as a real slave would when e.g.
        // the mailbox configuration it was just given is invalid.
        client.WriteAlControl(StationAddress, AlState.PreOp);

        slave.TransitionRefusal = (previousState, requestedState) =>
            requestedState == (ushort)AlState.SafeOp ? (ushort)0x0016 : null;

        client.WriteAlControl(StationAddress, AlState.SafeOp);
        var status = client.ReadAlStatus(StationAddress);

        Assert.Equal(AlState.PreOp, status.State); // refused: stays at the previous state.
        Assert.True(status.HasError);
        Assert.Equal((ushort)0x0016, status.StatusCode.Value);
        Assert.Equal("Invalid mailbox configuration (SafeOP)", status.StatusCode.Description);
    }

    [Fact]
    public void WriteFmmuConfig_then_ReadFmmuConfig_round_trips_through_the_fake_bus()
    {
        var (client, _, _) = CreateClientWithOneConfiguredSlave();

        var fmmu0 = FmmuConfig.ForByteAlignedRegion(0x00000000, 9, 0x1400, readEnabled: false, writeEnabled: true);
        client.WriteFmmuConfig(StationAddress, index: 0, fmmu0);

        Assert.Equal(fmmu0, client.ReadFmmuConfig(StationAddress, index: 0));
    }

    [Fact]
    public void WriteSmConfig_then_ReadSmConfig_round_trips_through_the_fake_bus()
    {
        var (client, _, _) = CreateClientWithOneConfiguredSlave();

        var sm2 = new SmConfig(0x1400, 9, ControlByte: 0x64);
        client.WriteSmConfig(StationAddress, index: 2, sm2);

        Assert.Equal(sm2, client.ReadSmConfig(StationAddress, index: 2));
    }
}
