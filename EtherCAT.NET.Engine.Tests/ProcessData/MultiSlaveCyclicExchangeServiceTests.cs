using System.Buffers.Binary;
using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Engine.Tests.Fakes;

namespace EtherCAT.NET.Engine.Tests.ProcessData;

/// <summary>
/// <see cref="MultiSlaveCyclicExchangeService"/> exercised against a <see cref="FakeBus"/> holding
/// TWO slaves (both matching the real, embedded Panasonic MADLN01BE ESI descriptor, brought to
/// SAFEOP exactly like <see cref="MultiSlaveAlStateMachine"/> would in production): the per-slave
/// mirror-actual-to-target safety invariant proven independent across two slaves with divergent
/// PositionActualValue sequences, <see cref="MultiSlaveCyclicExchangeService.SetControlword"/>
/// targeting exactly one slave and leaving every other slave untouched, the combined
/// (2-per-slave) Working Counter expectation, and the group-wide safe-shutdown behavior.
/// </summary>
public class MultiSlaveCyclicExchangeServiceTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private sealed record Fixture(
        FakeBus Bus,
        IReadOnlyList<FakeSlaveDevice> Slaves,
        MultiSlaveProcessImagePlan Plan,
        MultiSlaveCyclicExchangeService Service);

    /// <summary>
    /// Builds a fixture already at SAFEOP (FMMU0/FMMU1 + SM0-3 configured for every slave, exactly
    /// as <see cref="MultiSlaveAlStateMachine.TransitionToSafeOp"/> would leave it) with a fresh,
    /// not-yet-started <see cref="MultiSlaveCyclicExchangeService"/> wired to the same bus.
    /// </summary>
    private static Fixture CreateFixtureAtSafeOp(int slaveCount = 2)
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
            PollInterval = TimeSpan.Zero,
        };
        stateMachine.TransitionToPreOp(devices);
        stateMachine.TransitionToSafeOp(devices, plan);

        var service = new MultiSlaveCyclicExchangeService(transport, TestSourceMac, plan)
        {
            Period = TimeSpan.FromMilliseconds(1),
            ReplyTimeout = TimeSpan.FromMilliseconds(200),
        };

        return new Fixture(bus, slaves, plan, service);
    }

    private static int PhysicalAddress(MultiSlaveProcessImagePlan plan, int slaveIndex, bool outputs, ushort coeIndex)
    {
        var slave = plan.Slaves[slaveIndex];
        return outputs
            ? slave.OutputsFmmu.PhysicalStartAddress + slave.RxPdoLayout.GetEntry(coeIndex).ByteOffset
            : slave.InputsFmmu.PhysicalStartAddress + slave.TxPdoLayout.GetEntry(coeIndex).ByteOffset;
    }

    private static int ReadInt32(FakeSlaveDevice slave, int physicalAddress) =>
        BinaryPrimitives.ReadInt32LittleEndian(slave.ReadRegisterBytes((ushort)physicalAddress, 4));

    private static void WriteInt32(FakeSlaveDevice slave, int physicalAddress, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        slave.WriteRegisterBytes((ushort)physicalAddress, bytes);
    }

    private static ushort ReadUInt16(FakeSlaveDevice slave, int physicalAddress) =>
        BinaryPrimitives.ReadUInt16LittleEndian(slave.ReadRegisterBytes((ushort)physicalAddress, 2));

    [Fact]
    public void Start_wired_as_the_MultiSlaveAlStateMachine_onSafeOpRequested_hook_begins_running_immediately()
    {
        // CreateFixtureAtSafeOp requests SafeOp without wiring Start -- build the wiring explicitly
        // here instead, to exercise the exact callback path production code uses.
        var bus = new FakeBus();
        var slave0 = new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);
        var slave1 = new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);
        bus.AddSlave(slave0);
        bus.AddSlave(slave1);

        var transport = new FakeEthernetFrameTransport(bus);
        var escClient = new EscClient(transport, TestSourceMac);
        var esiLibrary = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();
        var discoveries = SlaveDiscovery.DiscoverAllSlaves(escClient, [esiLibrary]);
        var stationAddresses = discoveries.Select(d => d.StationAddress).ToList();
        var devices = discoveries.Select(d => d.Device).ToList();
        var plan = ProcessImageBuilder.BuildMulti(discoveries.Select(d => (d.StationAddress, d.Device)).ToList());

        var stateMachine = new MultiSlaveAlStateMachine(escClient, transport, TestSourceMac, stationAddresses) { PollInterval = TimeSpan.Zero };
        var service = new MultiSlaveCyclicExchangeService(transport, TestSourceMac, plan) { Period = TimeSpan.FromMilliseconds(1) };

        stateMachine.TransitionToPreOp(devices);

        Assert.False(service.IsRunning);
        stateMachine.TransitionToSafeOp(devices, plan, onSafeOpRequested: service.Start);
        Assert.True(service.IsRunning);

        service.Stop(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_on_a_never_started_service_is_a_harmless_no_op()
    {
        var fx = CreateFixtureAtSafeOp();

        fx.Service.Dispose();

        Assert.False(fx.Service.IsRunning);
    }

    [Fact]
    public void TargetPosition_mirrors_each_slaves_own_actual_value_independently_even_with_divergent_sequences()
    {
        var fx = CreateFixtureAtSafeOp();

        var targetPositionAddr = new[] { PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A), PhysicalAddress(fx.Plan, 1, outputs: true, 0x607A) };
        var actualValueAddr = new[] { PhysicalAddress(fx.Plan, 0, outputs: false, 0x6064), PhysicalAddress(fx.Plan, 1, outputs: false, 0x6064) };

        const int cyclesToObserve = 30;
        var priorActual = new[] { 0, 0 };
        // Deliberately very different ranges/steps per slave so any cross-wiring bug (slave 1
        // mirroring slave 0's actual value, or vice versa) would produce an obviously wrong number
        // rather than an accidental match.
        var nextActual = new[] { 1000, -50000 };
        var step = new[] { 37, -113 };

        var mismatches = new List<string>();
        var cyclesSeen = 0;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += _ =>
        {
            lock (mismatches)
            {
                if (cyclesSeen >= cyclesToObserve)
                {
                    return;
                }

                for (var i = 0; i < 2; i++)
                {
                    var writtenTarget = ReadInt32(fx.Slaves[i], targetPositionAddr[i]);
                    if (writtenTarget != priorActual[i])
                    {
                        mismatches.Add($"slave {i}, cycle {cyclesSeen}: target={writtenTarget}, expected prior actual={priorActual[i]}");
                    }

                    var actualJustRead = ReadInt32(fx.Slaves[i], actualValueAddr[i]);
                    priorActual[i] = actualJustRead;

                    WriteInt32(fx.Slaves[i], actualValueAddr[i], nextActual[i]);
                    nextActual[i] += step[i];
                }

                cyclesSeen++;
                if (cyclesSeen >= cyclesToObserve)
                {
                    done.Set();
                }
            }
        };

        fx.Service.Start();
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "Did not observe enough cycles in time.");
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.Empty(mismatches);
        // Sanity: both slaves' actual values genuinely diverged over the run, not a vacuous check.
        Assert.True(nextActual[0] > 1000);
        Assert.True(nextActual[1] < -50000);
    }

    [Fact]
    public void SetControlword_targets_only_the_specified_slave_index_leaving_every_other_slave_unchanged()
    {
        var fx = CreateFixtureAtSafeOp();
        var controlwordAddr = new[] { PhysicalAddress(fx.Plan, 0, outputs: true, 0x6040), PhysicalAddress(fx.Plan, 1, outputs: true, 0x6040) };

        var sawSlave0Shutdown = false;
        var done = new ManualResetEventSlim(initialState: false);
        var cycles = 0;

        fx.Service.StatusUpdated += _ =>
        {
            cycles++;
            if (cycles == 5)
            {
                fx.Service.SetControlword(0, Ds402Controlword.Shutdown);
            }

            var cw0 = ReadUInt16(fx.Slaves[0], controlwordAddr[0]);
            if (cw0 == Ds402Controlword.Shutdown)
            {
                sawSlave0Shutdown = true;
                done.Set();
            }
        };

        fx.Service.Start();
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.True(sawSlave0Shutdown);

        // Slave 1 was never targeted, so its Controlword must have stayed at the initial default
        // (DisableVoltage) right up until Stop()'s own final safe-shutdown write -- which also
        // happens to be DisableVoltage, so this remains true even after Stop.
        var cw1 = ReadUInt16(fx.Slaves[1], controlwordAddr[1]);
        Assert.Equal(Ds402Controlword.DisableVoltage, cw1);
    }

    [Fact]
    public void SetControlword_throws_for_a_slave_index_outside_the_group()
    {
        var fx = CreateFixtureAtSafeOp();

        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Service.SetControlword(2, Ds402Controlword.Shutdown));
        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Service.SetControlword(-1, Ds402Controlword.Shutdown));
    }

    [Fact]
    public void StatusUpdated_snapshot_carries_one_entry_per_slave_with_the_correct_station_addresses_and_combined_wkc()
    {
        var fx = CreateFixtureAtSafeOp();

        MultiSlaveProcessImageSnapshot? snapshot = null;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += s =>
        {
            if (s.IsDataFresh)
            {
                snapshot = s;
                done.Set();
            }
        };

        fx.Service.Start();
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(snapshot);
        Assert.Equal((ushort)4, snapshot!.LastWkc); // 2 per slave x 2 slaves.
        Assert.Equal(2, snapshot.Slaves.Count);
        Assert.Equal(fx.Plan.Slaves[0].StationAddress, snapshot.Slaves[0].StationAddress);
        Assert.Equal(fx.Plan.Slaves[1].StationAddress, snapshot.Slaves[1].StationAddress);
    }

    [Fact]
    public void A_forced_WKC_below_the_combined_expectation_surfaces_stale_data_and_eventually_faults()
    {
        var fx = CreateFixtureAtSafeOp();
        fx.Service.MaxConsecutiveFailures = 5;

        // Force a WKC that would be perfectly healthy for ONE slave (2) but is short of what this
        // TWO-slave group's combined expectation (4) actually requires.
        fx.Bus.WorkingCounterOverride = (datagram, wkc) =>
            datagram.Command == EtherCatCommand.Lrw ? (ushort)2 : wkc;

        var sawStaleData = false;
        string? faultedMessage = null;
        var faultedFired = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += s =>
        {
            if (!s.IsDataFresh)
            {
                sawStaleData = true;
                Assert.Equal((ushort)2, s.LastWkc);
                Assert.NotNull(s.LastError);
            }
        };
        fx.Service.Faulted += message =>
        {
            faultedMessage = message;
            faultedFired.Set();
        };

        fx.Service.Start();
        try
        {
            Assert.True(faultedFired.Wait(TimeSpan.FromSeconds(10)), "Faulted was never raised.");
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.True(sawStaleData);
        Assert.NotNull(faultedMessage);
        Assert.True(fx.Service.IsFaulted);
    }

    [Fact]
    public void Stop_sends_DisableVoltage_to_every_slave_before_the_thread_exits()
    {
        var fx = CreateFixtureAtSafeOp();
        fx.Service.SetControlword(0, Ds402Controlword.EnableOperation);
        fx.Service.SetControlword(1, Ds402Controlword.EnableOperation);

        var controlwordAddr = new[] { PhysicalAddress(fx.Plan, 0, outputs: true, 0x6040), PhysicalAddress(fx.Plan, 1, outputs: true, 0x6040) };

        var cycles = 0;
        var readyToStop = new ManualResetEventSlim(initialState: false);
        fx.Service.StatusUpdated += _ =>
        {
            if (Interlocked.Increment(ref cycles) == 5)
            {
                readyToStop.Set();
            }
        };

        fx.Service.Start();
        Assert.True(readyToStop.Wait(TimeSpan.FromSeconds(10)));

        fx.Service.Stop(TimeSpan.FromSeconds(2));

        Assert.Equal(Ds402Controlword.DisableVoltage, ReadUInt16(fx.Slaves[0], controlwordAddr[0]));
        Assert.Equal(Ds402Controlword.DisableVoltage, ReadUInt16(fx.Slaves[1], controlwordAddr[1]));
        Assert.False(fx.Service.IsRunning);
    }
}
