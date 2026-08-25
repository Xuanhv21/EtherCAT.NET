using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.StateMachine;

/// <summary>
/// <see cref="MultiSlaveAlStateMachine"/> exercised against a <see cref="FakeBus"/> holding TWO
/// slaves (both matching the real, embedded Panasonic MADLN01BE ESI descriptor — a realistic
/// two-identical-axis setup): the full group INIT-&gt;OP happy path, the exact per-slave SM0..SM3/
/// FMMU0/FMMU1 register contents, the group-wide "callback fires exactly once, after every slave has
/// had SAFEOP requested" timing contract, and an injected AL Status Code refusal on just one slave
/// surfacing a <see cref="MultiSlaveAlStateTransitionException"/> that identifies exactly which one.
/// </summary>
public class MultiSlaveAlStateMachineTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private sealed record Fixture(
        FakeBus Bus,
        IReadOnlyList<FakeSlaveDevice> Slaves,
        EscClient EscClient,
        MultiSlaveAlStateMachine StateMachine,
        IReadOnlyList<EsiDeviceDescriptor> Devices,
        MultiSlaveProcessImagePlan Plan,
        IReadOnlyList<ushort> StationAddresses);

    private static Fixture CreateFixture(int slaveCount = 2)
    {
        var bus = new FakeBus();
        var slaves = new List<FakeSlaveDevice>();
        for (var i = 0; i < slaveCount; i++)
        {
            var slave = new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);
            bus.AddSlave(slave);
            slaves.Add(slave);
        }

        var transport = new FakeEthernetFrameTransport(bus);
        var escClient = new EscClient(transport, TestSourceMac);

        var esiLibrary = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();
        var discoveries = SlaveDiscovery.DiscoverAllSlaves(escClient, [esiLibrary]);
        var devices = discoveries.Select(d => d.Device).ToList();
        var stationAddresses = discoveries.Select(d => d.StationAddress).ToList();
        var plan = ProcessImageBuilder.BuildMulti(discoveries.Select(d => (d.StationAddress, d.Device)).ToList());

        var stateMachine = new MultiSlaveAlStateMachine(escClient, transport, TestSourceMac, stationAddresses)
        {
            // The fake bus resolves everything synchronously and instantly; polling delays would
            // only slow the test down for no benefit.
            PollInterval = TimeSpan.Zero,
        };

        return new Fixture(bus, slaves, escClient, stateMachine, devices, plan, stationAddresses);
    }

    [Fact]
    public void BringUpToOp_drives_every_slave_in_the_group_from_Init_all_the_way_to_Op()
    {
        var fx = CreateFixture();
        Assert.All(fx.Slaves, s => Assert.Equal((ushort)AlState.Init, s.AlStatus));

        var callbackInvocations = 0;
        fx.StateMachine.BringUpToOp(fx.Devices, fx.Plan, onSafeOpRequested: () => callbackInvocations++);

        Assert.All(fx.Slaves, s => Assert.Equal((ushort)AlState.Op, s.AlStatus));
        Assert.Equal(1, callbackInvocations);
    }

    [Fact]
    public void TransitionToPreOp_writes_SM0_and_SM1_verbatim_for_every_slave_before_requesting_PreOp()
    {
        var fx = CreateFixture();

        fx.StateMachine.TransitionToPreOp(fx.Devices);

        for (var i = 0; i < fx.StationAddresses.Count; i++)
        {
            var sm0 = fx.EscClient.ReadSmConfig(fx.StationAddresses[i], 0);
            var sm1 = fx.EscClient.ReadSmConfig(fx.StationAddresses[i], 1);

            Assert.Equal(0x1000, sm0.PhysicalStartAddress);
            Assert.Equal((byte)0x26, sm0.ControlByte);
            Assert.Equal(0x1200, sm1.PhysicalStartAddress);
            Assert.Equal((byte)0x22, sm1.ControlByte);

            Assert.Equal((ushort)AlState.PreOp, fx.Slaves[i].AlStatus);
        }
    }

    [Fact]
    public void TransitionToSafeOp_writes_FMMU_and_SM_for_every_slave_and_fires_the_callback_exactly_once_after_all_are_requested()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Devices);

        var callbackCount = 0;
        var alControlAtCallbackTime = new List<ushort>();

        fx.StateMachine.TransitionToSafeOp(fx.Devices, fx.Plan, onSafeOpRequested: () =>
        {
            callbackCount++;
            // At the instant the callback fires, EVERY slave must already have had SAFEOP
            // requested -- not just the first one -- since the combined cyclic exchange this
            // callback starts covers the whole group in a single LRW.
            alControlAtCallbackTime.AddRange(fx.Slaves.Select(s => s.AlControl));
        });

        Assert.Equal(1, callbackCount);
        Assert.All(alControlAtCallbackTime, ac => Assert.Equal((ushort)AlState.SafeOp, ac));

        for (var i = 0; i < fx.StationAddresses.Count; i++)
        {
            var fmmu0 = fx.EscClient.ReadFmmuConfig(fx.StationAddresses[i], 0);
            var fmmu1 = fx.EscClient.ReadFmmuConfig(fx.StationAddresses[i], 1);

            Assert.Equal(fx.Plan.Slaves[i].OutputsFmmu, fmmu0);
            Assert.Equal(fx.Plan.Slaves[i].InputsFmmu, fmmu1);
            Assert.Equal((ushort)AlState.SafeOp, fx.Slaves[i].AlStatus);
        }
    }

    [Fact]
    public void TransitionToOp_requires_the_combined_working_counter_across_every_slave_before_requesting_Op()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Devices);
        fx.StateMachine.TransitionToSafeOp(fx.Devices, fx.Plan);
        fx.StateMachine.RequiredConsecutiveGoodExchanges = 3;

        Assert.Equal((ushort)4, fx.Plan.ExpectedWorkingCounter); // 2 slaves x 2 each.

        fx.StateMachine.TransitionToOp(fx.Plan);

        Assert.All(fx.Slaves, s => Assert.Equal((ushort)AlState.Op, s.AlStatus));
    }

    [Fact]
    public void TransitionToPreOp_throws_MultiSlaveAlStateTransitionException_identifying_exactly_which_slave_refused()
    {
        var fx = CreateFixture();

        // Only the SECOND slave (index 1) refuses PreOp; the first must be left correctly configured.
        fx.Slaves[1].TransitionRefusal = (_, requested) =>
            requested == (ushort)AlState.PreOp ? (ushort)0x0011 : null; // "Invalid requested state change"

        var ex = Assert.Throws<MultiSlaveAlStateTransitionException>(() => fx.StateMachine.TransitionToPreOp(fx.Devices));

        Assert.Equal(1, ex.SlaveIndex);
        Assert.Equal(fx.StationAddresses[1], ex.StationAddress);
        Assert.Equal(AlState.PreOp, ex.AttemptedState);
        Assert.Equal(AlState.Init, ex.ActualState);
        Assert.Equal((ushort)0x0011, ex.StatusCode.Value);
        Assert.False(ex.TimedOut);

        // The first slave's SM0/SM1 writes already happened (register writes are per slave and
        // unconditional before any AL Control is written), and it did successfully reach PreOp --
        // the group-wide failure of slave 1 does not retroactively undo slave 0's own state.
        Assert.Equal((ushort)AlState.PreOp, fx.Slaves[0].AlStatus);
    }

    [Fact]
    public void TransitionToSafeOp_throws_and_still_reports_the_callback_was_invoked_when_one_slave_refuses()
    {
        var fx = CreateFixture();
        fx.StateMachine.TransitionToPreOp(fx.Devices);

        fx.Slaves[0].TransitionRefusal = (_, requested) =>
            requested == (ushort)AlState.SafeOp ? (ushort)0x001B : null; // "Sync manager watchdog"

        var callbackInvoked = false;
        var ex = Assert.Throws<MultiSlaveAlStateTransitionException>(
            () => fx.StateMachine.TransitionToSafeOp(fx.Devices, fx.Plan, onSafeOpRequested: () => callbackInvoked = true));

        Assert.Equal(0, ex.SlaveIndex);
        Assert.Equal(fx.StationAddresses[0], ex.StationAddress);
        Assert.Equal(AlState.SafeOp, ex.AttemptedState);
        Assert.Equal(AlState.PreOp, ex.ActualState);

        // The callback's whole purpose is to fire the instant every slave's SAFEOP has been
        // requested (before any of them even has a chance to accept/refuse it), so a refusal
        // discovered afterwards, while polling, must not un-invoke it.
        Assert.True(callbackInvoked);
    }
}
