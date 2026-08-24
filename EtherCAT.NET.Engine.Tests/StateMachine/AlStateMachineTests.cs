using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.StateMachine;

/// <summary>
/// <see cref="AlStateMachine"/> exercised against <see cref="FakeBus"/>/<see cref="FakeSlaveDevice"/>
/// using the real, embedded Panasonic MADLN01BE ESI descriptor and its derived
/// <see cref="ProcessImagePlan"/> (not hand-written fixtures): the full INIT-&gt;OP happy path, the
/// exact SM0..SM3/FMMU0/FMMU1 register contents written along the way, the SAFEOP-request callback
/// timing contract, and an injected AL Status Code refusal at both PREOP and SAFEOP surfacing as a
/// typed <see cref="AlStateTransitionException"/> rather than a bare timeout.
/// </summary>
public class AlStateMachineTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private static EsiDeviceDescriptor GetMadln01Be()
    {
        var library = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();
        return library.Devices.Single(d =>
            d.Name == "MADLN01BE" &&
            d.ProductCode == Madln01BeProductCode &&
            d.RevisionNumber == Madln01BeRevision);
    }

    private sealed record Fixture(FakeBus Bus, FakeSlaveDevice Slave, EscClient EscClient, AlStateMachine StateMachine, EsiDeviceDescriptor Device, ProcessImagePlan Plan);

    private static Fixture CreateFixture()
    {
        var bus = new FakeBus();
        var slave = new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);
        bus.AddSlave(slave);

        var transport = new FakeEthernetFrameTransport(bus);
        var escClient = new EscClient(transport, TestSourceMac);

        var discovery = SlaveDiscovery.DiscoverSingleSlave(escClient, EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be());
        var plan = ProcessImageBuilder.BuildDefault(discovery.Device);

        var stateMachine = new AlStateMachine(escClient, transport, TestSourceMac, discovery.StationAddress)
        {
            // The fake bus resolves everything synchronously and instantly; polling delays would
            // only slow the test down for no benefit.
            PollInterval = TimeSpan.Zero,
        };

        return new Fixture(bus, slave, escClient, stateMachine, discovery.Device, plan);
    }

    [Fact]
    public void BringUpToOp_drives_the_real_MADLN01BE_device_from_Init_all_the_way_to_Op()
    {
        var fx = CreateFixture();

        Assert.Equal((ushort)AlState.Init, fx.Slave.AlStatus);

        var callbackInvocations = 0;
        var report = fx.StateMachine.BringUpToOp(fx.Device, fx.Plan, onSafeOpRequested: () => callbackInvocations++);

        Assert.Equal(AlState.Op, report.State);
        Assert.False(report.HasError);
        Assert.Equal(AlStatusCode.NoError, report.StatusCode);
        Assert.Equal((ushort)AlState.Op, fx.Slave.AlStatus);
        Assert.Equal(1, callbackInvocations);
    }

    [Fact]
    public void TransitionToPreOp_writes_SM0_and_SM1_verbatim_from_the_ESI_before_requesting_PreOp()
    {
        var fx = CreateFixture();

        fx.StateMachine.TransitionToPreOp(fx.Device);

        var sm0 = fx.EscClient.ReadSmConfig(SlaveDiscovery.FirstStationAddress, 0);
        var sm1 = fx.EscClient.ReadSmConfig(SlaveDiscovery.FirstStationAddress, 1);

        Assert.Equal(0x1000, sm0.PhysicalStartAddress);
        Assert.Equal((ushort)256, sm0.Length);
        Assert.Equal((byte)0x26, sm0.ControlByte);

        Assert.Equal(0x1200, sm1.PhysicalStartAddress);
        Assert.Equal((ushort)256, sm1.Length);
        Assert.Equal((byte)0x22, sm1.ControlByte);

        Assert.Equal((ushort)AlState.PreOp, fx.Slave.AlStatus);
    }

    [Fact]
    public void TransitionToSafeOp_writes_FMMU0_FMMU1_and_SM2_SM3_verbatim_before_requesting_SafeOp()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Device);

        fx.StateMachine.TransitionToSafeOp(fx.Device, fx.Plan);

        var fmmu0 = fx.EscClient.ReadFmmuConfig(SlaveDiscovery.FirstStationAddress, 0);
        var fmmu1 = fx.EscClient.ReadFmmuConfig(SlaveDiscovery.FirstStationAddress, 1);
        var sm2 = fx.EscClient.ReadSmConfig(SlaveDiscovery.FirstStationAddress, 2);
        var sm3 = fx.EscClient.ReadSmConfig(SlaveDiscovery.FirstStationAddress, 3);

        Assert.Equal(fx.Plan.OutputsFmmu, fmmu0);
        Assert.Equal(fx.Plan.InputsFmmu, fmmu1);

        Assert.Equal(0x1400, sm2.PhysicalStartAddress);
        Assert.Equal((ushort)9, sm2.Length);
        Assert.Equal((byte)0x64, sm2.ControlByte);

        Assert.Equal(0x1600, sm3.PhysicalStartAddress);
        Assert.Equal((ushort)23, sm3.Length);
        Assert.Equal((byte)0x20, sm3.ControlByte);

        Assert.Equal((ushort)AlState.SafeOp, fx.Slave.AlStatus);
    }

    [Fact]
    public void TransitionToSafeOp_invokes_the_callback_only_after_FMMU_SM_configuration_and_the_SafeOp_request_have_already_been_written()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Device);

        var callbackCount = 0;
        FmmuConfig? fmmu0AtCallbackTime = null;
        ushort? alControlAtCallbackTime = null;

        fx.StateMachine.TransitionToSafeOp(fx.Device, fx.Plan, onSafeOpRequested: () =>
        {
            callbackCount++;
            fmmu0AtCallbackTime = fx.EscClient.ReadFmmuConfig(SlaveDiscovery.FirstStationAddress, 0);
            alControlAtCallbackTime = fx.Slave.AlControl;
        });

        Assert.Equal(1, callbackCount);
        Assert.Equal(fx.Plan.OutputsFmmu, fmmu0AtCallbackTime);
        Assert.Equal((ushort)AlState.SafeOp, alControlAtCallbackTime);
    }

    [Fact]
    public void TransitionToOp_only_requests_Op_after_enough_consecutive_good_LRW_exchanges()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Device);
        fx.StateMachine.TransitionToSafeOp(fx.Device, fx.Plan);
        fx.StateMachine.RequiredConsecutiveGoodExchanges = 3;

        var report = fx.StateMachine.TransitionToOp(fx.Plan);

        Assert.Equal(AlState.Op, report.State);
    }

    [Fact]
    public void TransitionToPreOp_throws_AlStateTransitionException_carrying_the_exact_injected_AL_Status_Code()
    {
        var fx = CreateFixture();
        fx.Slave.TransitionRefusal = (_, requested) =>
            requested == (ushort)AlState.PreOp ? (ushort)0x0011 : null; // "Invalid requested state change"

        var ex = Assert.Throws<AlStateTransitionException>(() => fx.StateMachine.TransitionToPreOp(fx.Device));

        Assert.Equal(AlState.PreOp, ex.AttemptedState);
        Assert.Equal(AlState.Init, ex.ActualState);
        Assert.Equal((ushort)0x0011, ex.StatusCode.Value);
        Assert.Equal("Invalid requested state change", ex.StatusCode.Description);
        Assert.False(ex.TimedOut);
    }

    [Fact]
    public void TransitionToSafeOp_throws_AlStateTransitionException_carrying_the_exact_injected_AL_Status_Code_instead_of_a_generic_timeout()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Device);

        fx.Slave.TransitionRefusal = (_, requested) =>
            requested == (ushort)AlState.SafeOp ? (ushort)0x001B : null; // "Sync manager watchdog"

        var callbackInvoked = false;
        var ex = Assert.Throws<AlStateTransitionException>(
            () => fx.StateMachine.TransitionToSafeOp(fx.Device, fx.Plan, onSafeOpRequested: () => callbackInvoked = true));

        Assert.Equal(AlState.SafeOp, ex.AttemptedState);
        Assert.Equal(AlState.PreOp, ex.ActualState); // refused: slave stays at the previous state.
        Assert.Equal((ushort)0x001B, ex.StatusCode.Value);
        Assert.Equal("Sync manager watchdog", ex.StatusCode.Description);
        Assert.False(ex.TimedOut);

        // The callback's whole purpose is to fire the instant SAFEOP is requested (before the ESC
        // even has a chance to accept/refuse it), so a refusal afterwards must not un-invoke it.
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void BringUpToOp_stops_at_SafeOp_and_never_requests_Op_when_SafeOp_is_refused()
    {
        var fx = CreateFixture();
        fx.Slave.TransitionRefusal = (_, requested) =>
            requested == (ushort)AlState.SafeOp ? (ushort)0x0017 : null; // "Invalid sync manager configuration"

        Assert.Throws<AlStateTransitionException>(() => fx.StateMachine.BringUpToOp(fx.Device, fx.Plan));

        // AL Control reflects the last *requested* state (SafeOp) even though it was refused; AL
        // Status (decoded, ignoring the Error flag bit) is what actually matters here -- the slave
        // never got past PreOp, so it never even got a chance to have Op requested of it.
        var status = fx.EscClient.ReadAlStatus(SlaveDiscovery.FirstStationAddress);
        Assert.Equal(AlState.PreOp, status.State);
        Assert.True(status.HasError);
    }
}
