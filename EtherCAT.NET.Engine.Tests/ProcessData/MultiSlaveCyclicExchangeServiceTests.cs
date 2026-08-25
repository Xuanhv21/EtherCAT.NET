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

    // --- SetJog: bounded, heartbeat-gated, Operation-enabled-gated position jog. ---

    private const ushort OperationEnabledStatusword = 0x0037; // Ready to switch on + Switched on + Operation enabled + Voltage enabled + Quick stop.

    private static void SeedOperationEnabledStatusword(FakeSlaveDevice slave, MultiSlaveProcessImagePlan plan, int slaveIndex)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, OperationEnabledStatusword);
        slave.WriteRegisterBytes((ushort)PhysicalAddress(plan, slaveIndex, outputs: false, 0x6041), bytes);
    }

    /// <summary>
    /// Every test below collects raw data from <see cref="MultiSlaveCyclicExchangeService.StatusUpdated"/>
    /// (which fires on the cyclic background thread) and asserts on it only AFTER <c>Stop()</c>
    /// returns, back on the test thread -- never inside the handler itself. An <c>Assert</c> failure
    /// inside a background-thread callback is an unhandled exception on that thread, which crashes
    /// the whole test host rather than failing just one test.
    /// </summary>
    [Fact]
    public void SetJog_has_no_effect_on_TargetPosition_or_ModesOfOperation_before_OperationEnabled_is_observed()
    {
        var fx = CreateFixtureAtSafeOp();
        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var modesAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x6060);

        fx.Service.SetJog(0, +1);

        var cycles = 0;
        var done = new ManualResetEventSlim(initialState: false);
        var everSawNonZeroTarget = false;
        var everSawNonZeroModes = false;
        var everSawIsJoggingTrue = false;

        fx.Service.StatusUpdated += s =>
        {
            everSawNonZeroTarget |= ReadInt32(fx.Slaves[0], targetAddr) != 0;
            everSawNonZeroModes |= fx.Slaves[0].ReadRegisterBytes((ushort)modesAddr, 1)[0] != 0;
            everSawIsJoggingTrue |= s.Slaves[0].IsJogging;

            if (Interlocked.Increment(ref cycles) >= 20)
            {
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

        // Statusword was never seeded as Operation-enabled (defaults to 0 = Switch on disabled), so
        // the held jog request must never actually have moved anything.
        Assert.False(everSawNonZeroTarget);
        Assert.False(everSawNonZeroModes);
        Assert.False(everSawIsJoggingTrue);
    }

    /// <summary>
    /// Acceleration/deceleration set high enough (relative to the fixture's 1 ms test cycle period
    /// and to every <c>JogVelocity</c> used below, all &lt;= 10,000) that a slave's internal jog
    /// velocity reaches <see cref="MultiSlaveCyclicExchangeService.JogVelocity"/> (or 0, on release)
    /// in a single cycle -- i.e. effectively instant ramping: this must clear
    /// <c>JogVelocity / cyclePeriodSeconds</c> (10,000 / 0.001 s = 10,000,000) with a healthy margin.
    /// Used by tests whose point is the bounded/gating/latching behavior itself, not the ramp shape
    /// (which has its own dedicated tests below).
    /// </summary>
    private const double InstantRamp = 100_000_000.0;

    [Fact]
    public void SetJog_offsets_TargetPosition_by_exactly_one_bounded_step_re_based_on_actual_every_cycle()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);
        fx.Service.JogVelocity = 7000; // units/sec; at this fixture's 1 ms cycle period, exactly 7 units/cycle once ramped.
        fx.Service.JogAcceleration = InstantRamp;
        fx.Service.JogDeceleration = InstantRamp;

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var observedTargets = new List<int>();
        var observedIsJogging = new List<bool>();
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.SetJog(0, +1);

        fx.Service.StatusUpdated += s =>
        {
            lock (observedTargets)
            {
                if (observedTargets.Count >= 20)
                {
                    return;
                }

                observedTargets.Add(ReadInt32(fx.Slaves[0], targetAddr));
                observedIsJogging.Add(s.Slaves[0].IsJogging);

                if (observedTargets.Count >= 20)
                {
                    done.Set();
                }
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

        // Cycle 1 necessarily still mirrors actual (0) with jog withheld: OperationEnabled is only
        // knowable from the PREVIOUS cycle's own successful exchange, and there is no previous cycle
        // yet -- the exact same one-cycle lag the single-slave CyclicExchangeServiceTests documents
        // for its own mirror-actual-to-target invariant. From cycle 2 onward, OperationEnabled has
        // been observed and jog is live: the fake slave never moves its own PositionActualValue on
        // its own (there is no simulated motor), so with the target re-based on actual every cycle
        // rather than compounded on the previous target, it must stay flat at exactly one step away
        // from actual (0) -- never growing cycle over cycle. A flawed "accumulate onto the previous
        // target" implementation would instead show 7, 14, 21, ... here.
        Assert.Equal(0, observedTargets[0]);
        Assert.False(observedIsJogging[0]);
        Assert.All(observedTargets.Skip(1), t => Assert.Equal(7, t));
        Assert.All(observedIsJogging.Skip(1), j => Assert.True(j));
    }

    [Fact]
    public void SetJog_negative_direction_offsets_the_other_way()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);
        fx.Service.JogVelocity = 5000; // 5 units/cycle at 1 ms.
        fx.Service.JogAcceleration = InstantRamp;
        fx.Service.JogDeceleration = InstantRamp;

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var cycles = 0;
        var observedTarget = int.MinValue;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.SetJog(0, -1);
        fx.Service.StatusUpdated += _ =>
        {
            // Cycle 1 still lags (see the test above) -- wait for a cycle well past the point
            // OperationEnabled is guaranteed to have been observed already.
            if (Interlocked.Increment(ref cycles) == 5)
            {
                observedTarget = ReadInt32(fx.Slaves[0], targetAddr);
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

        Assert.Equal(-5, observedTarget);
    }

    [Fact]
    public void SetJog_releasing_with_instant_deceleration_immediately_reverts_to_mirroring_actual()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);
        fx.Service.JogVelocity = 9000; // 9 units/cycle at 1 ms.
        fx.Service.JogAcceleration = InstantRamp;
        fx.Service.JogDeceleration = InstantRamp; // release must drop straight to 0, not a gradual ramp -- see the dedicated ramp-down test for that.

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var cycles = 0;
        var releasedAt = -1;
        var done = new ManualResetEventSlim(initialState: false);
        var targetAfterRelease = int.MinValue;

        fx.Service.SetJog(0, +1);
        fx.Service.StatusUpdated += _ =>
        {
            cycles++;
            if (cycles == 5)
            {
                fx.Service.SetJog(0, 0);
                releasedAt = cycles;
            }
            else if (releasedAt >= 0 && cycles == releasedAt + 1)
            {
                targetAfterRelease = ReadInt32(fx.Slaves[0], targetAddr);
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

        Assert.Equal(0, targetAfterRelease); // back to mirroring actual (still 0 in this fake) immediately.
    }

    [Fact]
    public void SetJog_auto_releases_after_the_heartbeat_times_out_without_renewal()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);
        fx.Service.JogVelocity = 3000; // 3 units/cycle at 1 ms.
        fx.Service.JogAcceleration = InstantRamp;
        fx.Service.JogDeceleration = InstantRamp; // the heartbeat-expiry auto-release must drop straight to 0.
        fx.Service.JogHeartbeatTimeout = TimeSpan.FromMilliseconds(30);

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var logs = new List<string>();
        fx.Service.LogEmitted += logs.Add;

        fx.Service.SetJog(0, +1); // set once, deliberately never renewed.

        var sawJoggingApplied = false;
        var revertedToZero = false;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += s =>
        {
            var current = ReadInt32(fx.Slaves[0], targetAddr);
            if (current == 3)
            {
                sawJoggingApplied = true;
            }

            if (sawJoggingApplied && current == 0)
            {
                revertedToZero = true;
                done.Set();
            }
        };

        fx.Service.Start();
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "Jog never auto-released after the heartbeat timeout.");
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.True(sawJoggingApplied);
        Assert.True(revertedToZero);
        Assert.Contains(logs, l => l.Contains("jog heartbeat timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetJog_switches_ModesOfOperation_to_CSP_permanently_once_engaged_even_after_releasing()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);

        var modesAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x6060);
        sbyte ReadModes() => unchecked((sbyte)fx.Slaves[0].ReadRegisterBytes((ushort)modesAddr, 1)[0]);

        var cycles = 0;
        var modesBeforeJog = (sbyte)99; // sentinel.
        var modesAfterRelease = (sbyte)99;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += _ =>
        {
            cycles++;
            if (cycles == 1)
            {
                modesBeforeJog = ReadModes();
            }
            else if (cycles == 2)
            {
                fx.Service.SetJog(0, +1);
            }
            else if (cycles == 6)
            {
                fx.Service.SetJog(0, 0); // release.
            }
            else if (cycles == 10)
            {
                modesAfterRelease = ReadModes();
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

        Assert.Equal((sbyte)0, modesBeforeJog); // never jogged yet -- still the Milestone 1 default.
        Assert.Equal((sbyte)8, modesAfterRelease); // latched CSP even though jog was released 4 cycles earlier.
    }

    [Fact]
    public void SetJog_ramps_velocity_up_gradually_bounded_by_JogAcceleration_rather_than_jumping_instantly()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);

        // Chosen so every intermediate ramp step lands on an exact whole-count increment at this
        // fixture's 1 ms cycle period, with no fractional-remainder rounding to obscure the shape:
        // maxDelta/cycle = 1,000,000 * 0.001 s = 1000 units/sec, so velocity (and therefore the
        // whole-count increment, velocity * 0.001 s) climbs by exactly 1 unit/cycle: 1, 2, 3, ...
        // until it clamps at JogVelocity's own 10 units/cycle.
        fx.Service.JogVelocity = 10_000;
        fx.Service.JogAcceleration = 1_000_000;
        fx.Service.JogDeceleration = InstantRamp; // not exercised in this test.

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var observedTargets = new List<int>();
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.SetJog(0, +1);
        fx.Service.StatusUpdated += _ =>
        {
            lock (observedTargets)
            {
                if (observedTargets.Count >= 15)
                {
                    return;
                }

                observedTargets.Add(ReadInt32(fx.Slaves[0], targetAddr));
                if (observedTargets.Count >= 15)
                {
                    done.Set();
                }
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

        // Cycle 1 (index 0): still the one-cycle OperationEnabled lag documented above -- 0.
        // Cycles 2-11 (indices 1-10): the ramp itself, exactly 1 unit/cycle faster each cycle.
        // Cycles 12+ (indices 11-14): clamped at JogVelocity's steady-state 10 units/cycle.
        // A flawed "instant jump to full speed" implementation would instead show 0, 10, 10, 10, ...
        Assert.Equal(0, observedTargets[0]);
        for (var step = 1; step <= 10; step++)
        {
            Assert.Equal(step, observedTargets[step]);
        }

        Assert.All(observedTargets.Skip(11), t => Assert.Equal(10, t));
    }

    [Fact]
    public void SetJog_ramps_velocity_down_gradually_on_release_bounded_by_JogDeceleration_rather_than_stopping_instantly()
    {
        var fx = CreateFixtureAtSafeOp();
        SeedOperationEnabledStatusword(fx.Slaves[0], fx.Plan, 0);

        // Same construction as the ramp-up test, but with a deceleration rate (2000 units/sec/cycle)
        // that also lands on exact whole counts: 10 -> 8 -> 6 -> 4 -> 2 -> 0.
        fx.Service.JogVelocity = 10_000;
        fx.Service.JogAcceleration = 1_000_000; // reach steady state almost immediately, so this test isolates the release/decel shape.
        fx.Service.JogDeceleration = 2_000_000;

        var targetAddr = PhysicalAddress(fx.Plan, 0, outputs: true, 0x607A);
        var afterReleaseTargets = new List<int>();
        var released = false;
        var cycles = 0;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.SetJog(0, +1);
        fx.Service.StatusUpdated += _ =>
        {
            lock (afterReleaseTargets)
            {
                cycles++;

                // Let the ramp-up fully settle at steady state (10 units/cycle, reached by cycle 11
                // per the ramp-up test above) well before releasing.
                if (cycles == 20)
                {
                    fx.Service.SetJog(0, 0);
                    released = true;
                    return;
                }

                if (!released)
                {
                    return;
                }

                if (afterReleaseTargets.Count >= 8)
                {
                    return;
                }

                afterReleaseTargets.Add(ReadInt32(fx.Slaves[0], targetAddr));
                if (afterReleaseTargets.Count >= 8)
                {
                    done.Set();
                }
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

        // The cycle right after SetJog(0,0) still decelerates gradually -- 8, 6, 4, 2, 0 -- rather
        // than dropping straight to 0. A flawed "instant stop" implementation would instead show
        // 0, 0, 0, 0, 0, 0, 0, 0 here.
        Assert.Equal(new[] { 8, 6, 4, 2, 0, 0, 0, 0 }, afterReleaseTargets);
    }

    [Fact]
    public void SetJog_throws_for_an_invalid_slave_index_or_direction()
    {
        var fx = CreateFixtureAtSafeOp();

        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Service.SetJog(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Service.SetJog(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => fx.Service.SetJog(0, -2));
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
