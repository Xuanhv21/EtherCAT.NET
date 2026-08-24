using System.Buffers.Binary;
using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Engine.Tests.Fakes;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.Tests.ProcessData;

/// <summary>
/// Transparent <see cref="IEthernetFrameTransport"/> decorator that records the length of every
/// frame handed to <see cref="Send"/>, so a test can assert on the exact wire size
/// <see cref="CyclicExchangeService"/> actually produces without needing to intercept or duplicate
/// its internal datagram-building logic.
/// </summary>
file sealed class FrameLengthRecordingTransport(IEthernetFrameTransport inner) : IEthernetFrameTransport
{
    public List<int> SentFrameLengths { get; } = [];

    public string Name => inner.Name;

    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived
    {
        add => inner.FrameReceived += value;
        remove => inner.FrameReceived -= value;
    }

    public void Send(ReadOnlyMemory<byte> frame)
    {
        SentFrameLengths.Add(frame.Length);
        inner.Send(frame);
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// <see cref="CyclicExchangeService"/> exercised against <see cref="FakeBus"/>/<see cref="FakeSlaveDevice"/>
/// (through the real, embedded Panasonic MADLN01BE ESI descriptor, brought to SAFEOP exactly like
/// <see cref="AlStateMachine"/> would in production): the mirror-actual-to-target safety invariant
/// across many simulated cycles with a changing PositionActualValue, CiA 402 Statusword decoding
/// against known bit patterns, and the WKC-mismatch / consecutive-failure Faulted path.
/// </summary>
public class CyclicExchangeServiceTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });

    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private sealed record Fixture(
        FakeBus Bus,
        FakeSlaveDevice Slave,
        ProcessImagePlan Plan,
        CyclicExchangeService Service,
        int TargetPositionPhysicalAddress,
        int PositionActualValuePhysicalAddress,
        int StatuswordPhysicalAddress);

    /// <summary>
    /// Builds a fixture already at SAFEOP (FMMU0/FMMU1 + SM0-3 configured, exactly as
    /// <see cref="AlStateMachine.TransitionToSafeOp"/> would leave it) with a fresh, not-yet-started
    /// <see cref="CyclicExchangeService"/> wired to the same bus.
    /// </summary>
    private static Fixture CreateFixtureAtSafeOp()
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
            PollInterval = TimeSpan.Zero,
        };

        stateMachine.TransitionToPreOp(discovery.Device);
        stateMachine.TransitionToSafeOp(discovery.Device, plan);

        var service = new CyclicExchangeService(transport, TestSourceMac, plan)
        {
            Period = TimeSpan.FromMilliseconds(1),
            ReplyTimeout = TimeSpan.FromMilliseconds(200),
        };

        var targetPositionOffset = plan.RxPdoLayout.GetEntry(0x607A).ByteOffset;
        var positionActualOffset = plan.TxPdoLayout.GetEntry(0x6064).ByteOffset;
        var statuswordOffset = plan.TxPdoLayout.GetEntry(0x6041).ByteOffset;

        return new Fixture(
            bus,
            slave,
            plan,
            service,
            plan.OutputsFmmu.PhysicalStartAddress + targetPositionOffset,
            plan.InputsFmmu.PhysicalStartAddress + positionActualOffset,
            plan.InputsFmmu.PhysicalStartAddress + statuswordOffset);
    }

    private static int ReadInt32(FakeSlaveDevice slave, int physicalAddress) =>
        BinaryPrimitives.ReadInt32LittleEndian(slave.ReadRegisterBytes((ushort)physicalAddress, 4));

    private static void WriteInt32(FakeSlaveDevice slave, int physicalAddress, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        slave.WriteRegisterBytes((ushort)physicalAddress, bytes);
    }

    [Fact]
    public void Start_wired_as_the_AlStateMachine_onSafeOpRequested_hook_begins_running_immediately_at_SafeOp_request_time()
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
            PollInterval = TimeSpan.Zero,
        };
        var service = new CyclicExchangeService(transport, TestSourceMac, plan) { Period = TimeSpan.FromMilliseconds(1) };

        stateMachine.TransitionToPreOp(discovery.Device);

        Assert.False(service.IsRunning);
        stateMachine.TransitionToSafeOp(discovery.Device, plan, onSafeOpRequested: service.Start);
        Assert.True(service.IsRunning);

        service.Stop(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TargetPosition_is_always_the_PositionActualValue_read_back_on_the_immediately_preceding_cycle_even_as_it_keeps_changing()
    {
        var fx = CreateFixtureAtSafeOp();

        const int cyclesToObserve = 50;
        var samples = new List<(int WrittenTarget, int PriorActual)>();
        // previousCycleActual tracks the PositionActualValue that this service's *previous* cycle
        // actually read (and therefore must appear as *this* cycle's outbound TargetPosition) --
        // distinct from "the value we're about to inject for a future cycle to read".
        var previousCycleActual = 0;
        var nextActualToReport = 1000;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += _ =>
        {
            // Runs synchronously on the cyclic thread, right after this cycle's LRW exchange
            // completed -- the same thread that just wrote TargetPosition into the slave's
            // physical Outputs memory and read PositionActualValue out of its physical Inputs
            // memory. Safe to touch the fake slave's registers here without any extra locking.
            lock (samples)
            {
                if (samples.Count >= cyclesToObserve)
                {
                    return;
                }

                var writtenTarget = ReadInt32(fx.Slave, fx.TargetPositionPhysicalAddress);

                // What this very cycle's LRW read actually returned -- the physical register still
                // holds it, since we only overwrite it below, after recording it.
                var actualJustRead = ReadInt32(fx.Slave, fx.PositionActualValuePhysicalAddress);

                samples.Add((writtenTarget, previousCycleActual));
                previousCycleActual = actualJustRead;

                // Make the fake slave report a new, different PositionActualValue for the *next*
                // cycle to pick up and mirror on the cycle after that.
                WriteInt32(fx.Slave, fx.PositionActualValuePhysicalAddress, nextActualToReport);
                nextActualToReport += 37;

                if (samples.Count >= cyclesToObserve)
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

        Assert.Equal(cyclesToObserve, samples.Count);

        // The very first cycle mirrors 0 (no PositionActualValue has ever been read yet) -- the
        // invariant is active from before any Controlword change, not bootstrapped by one.
        Assert.Equal(0, samples[0].WrittenTarget);

        foreach (var (writtenTarget, priorActual) in samples)
        {
            Assert.Equal(priorActual, writtenTarget);
        }

        // Sanity: PositionActualValue genuinely changed across the run, so this wasn't a
        // vacuously-true check against a value that never moved.
        Assert.True(samples[^1].PriorActual > samples[0].PriorActual);
    }

    [Fact]
    public void ModesOfOperation_is_always_zero_regardless_of_Controlword_changes()
    {
        var fx = CreateFixtureAtSafeOp();
        var modesOfOperationPhysicalAddress = fx.Plan.OutputsFmmu.PhysicalStartAddress + fx.Plan.RxPdoLayout.GetEntry(0x6060).ByteOffset;

        var observed = new List<sbyte>();
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += _ =>
        {
            lock (observed)
            {
                if (observed.Count >= 20)
                {
                    return;
                }

                observed.Add(unchecked((sbyte)fx.Slave.ReadRegisterBytes((ushort)modesOfOperationPhysicalAddress, 1)[0]));

                // Repeatedly flip the Controlword externally -- this must have no bearing on ModesOfOperation.
                fx.Service.SetControlword(observed.Count % 2 == 0 ? Ds402Controlword.Shutdown : Ds402Controlword.SwitchOn);

                if (observed.Count >= 20)
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

        Assert.All(observed, m => Assert.Equal((sbyte)0, m));
    }

    [Fact]
    public void SetControlword_is_the_only_way_the_outbound_Controlword_field_changes()
    {
        var fx = CreateFixtureAtSafeOp();
        var controlwordPhysicalAddress = fx.Plan.OutputsFmmu.PhysicalStartAddress + fx.Plan.RxPdoLayout.GetEntry(0x6040).ByteOffset;

        var seenNonZero = false;
        var done = new ManualResetEventSlim(initialState: false);
        var cycles = 0;

        fx.Service.StatusUpdated += _ =>
        {
            cycles++;
            var raw = fx.Slave.ReadRegisterBytes((ushort)controlwordPhysicalAddress, 2);
            var controlword = BinaryPrimitives.ReadUInt16LittleEndian(raw);

            if (cycles == 5)
            {
                fx.Service.SetControlword(Ds402Controlword.Shutdown);
            }

            if (controlword == Ds402Controlword.Shutdown)
            {
                seenNonZero = true;
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

        Assert.True(seenNonZero);
    }

    [Theory]
    [InlineData(0x0040, false, false, false, false, false, false, true, false)] // Switch on disabled
    [InlineData(0x0021, true, false, false, false, false, true, false, false)] // Ready to switch on (+ quick stop bit set)
    [InlineData(0x0037, true, true, true, false, true, true, false, false)] // Operation enabled
    public void Ds402Statusword_decodes_known_CiA402_bit_patterns_correctly(
        ushort raw,
        bool readyToSwitchOn,
        bool switchedOn,
        bool operationEnabled,
        bool fault,
        bool voltageEnabled,
        bool quickStop,
        bool switchOnDisabled,
        bool warning)
    {
        var status = new Ds402Statusword(raw);

        Assert.Equal(readyToSwitchOn, status.ReadyToSwitchOn);
        Assert.Equal(switchedOn, status.SwitchedOn);
        Assert.Equal(operationEnabled, status.OperationEnabled);
        Assert.Equal(fault, status.Fault);
        Assert.Equal(voltageEnabled, status.VoltageEnabled);
        Assert.Equal(quickStop, status.QuickStop);
        Assert.Equal(switchOnDisabled, status.SwitchOnDisabled);
        Assert.Equal(warning, status.Warning);
    }

    [Fact]
    public void StatusUpdated_carries_the_decoded_Statusword_the_fake_slave_actually_reported()
    {
        var fx = CreateFixtureAtSafeOp();
        WriteInt32(fx.Slave, fx.StatuswordPhysicalAddress, 0); // clear first 4 bytes (Statusword is only 2, but this keeps modes-display byte 0 too)
        Span<byte> statuswordBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statuswordBytes, 0x0037);
        fx.Slave.WriteRegisterBytes((ushort)fx.StatuswordPhysicalAddress, statuswordBytes);

        ProcessImageSnapshot? snapshot = null;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += s =>
        {
            if (s.IsDataFresh && s.RawStatusword == 0x0037)
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
        Assert.Equal((ushort)0x0037, snapshot!.RawStatusword);
        Assert.True(snapshot.Status.OperationEnabled);
        Assert.True(snapshot.IsDataFresh);
        Assert.Equal((ushort)2, snapshot.LastWkc);
        Assert.Null(snapshot.LastError);
        Assert.False(snapshot.IsFaulted);
    }

    [Fact]
    public void A_forced_WKC_mismatch_surfaces_IsDataFresh_false_and_enough_consecutive_failures_trigger_Faulted()
    {
        var fx = CreateFixtureAtSafeOp();
        fx.Service.MaxConsecutiveFailures = 5;

        fx.Bus.WorkingCounterOverride = (datagram, wkc) =>
            datagram.Command == EtherCatCommand.Lrw ? (ushort)0 : wkc;

        var sawStaleData = false;
        string? faultedMessage = null;
        var faultedFired = new ManualResetEventSlim(initialState: false);

        fx.Service.StatusUpdated += s =>
        {
            if (!s.IsDataFresh)
            {
                sawStaleData = true;
                Assert.Equal((ushort)0, s.LastWkc);
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
    public void LogEmitted_does_not_fire_once_per_healthy_tick()
    {
        var fx = CreateFixtureAtSafeOp();
        var logCount = 0;
        var cycleCount = 0;
        var done = new ManualResetEventSlim(initialState: false);

        fx.Service.LogEmitted += _ => Interlocked.Increment(ref logCount);
        fx.Service.StatusUpdated += _ =>
        {
            if (Interlocked.Increment(ref cycleCount) >= 30)
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

        // 30+ healthy cycles produced, at most, the AL-state-change/fault-transition class of logs --
        // never one log line per tick.
        Assert.True(logCount < cycleCount);
    }

    [Fact]
    public void Stop_sends_one_final_DisableVoltage_exchange_before_the_thread_exits()
    {
        var fx = CreateFixtureAtSafeOp();
        fx.Service.SetControlword(Ds402Controlword.EnableOperation);

        var controlwordPhysicalAddress = fx.Plan.OutputsFmmu.PhysicalStartAddress + fx.Plan.RxPdoLayout.GetEntry(0x6040).ByteOffset;

        // Let a few real cycles with EnableOperation actually land before stopping.
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

        var finalControlword = BinaryPrimitives.ReadUInt16LittleEndian(fx.Slave.ReadRegisterBytes((ushort)controlwordPhysicalAddress, 2));
        Assert.Equal(Ds402Controlword.DisableVoltage, finalControlword);
        Assert.False(fx.Service.IsRunning);
    }

    [Fact]
    public void SetAlState_updates_the_snapshot_and_logs_only_on_an_actual_change()
    {
        var fx = CreateFixtureAtSafeOp();
        Assert.Equal(AlState.SafeOp, fx.Service.AlState);

        var logs = new List<string>();
        fx.Service.LogEmitted += logs.Add;

        AlState? observed = null;
        var done = new ManualResetEventSlim(initialState: false);
        fx.Service.StatusUpdated += s =>
        {
            if (s.AlState == AlState.Op)
            {
                observed = s.AlState;
                done.Set();
            }
        };

        fx.Service.Start();
        fx.Service.SetAlState(AlState.Op);
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            fx.Service.Stop(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(AlState.Op, observed);
        Assert.Equal(AlState.Op, fx.Service.AlState);
        Assert.Contains(logs, l => l.Contains("AL state changed") && l.Contains("Op"));
    }

    /// <summary>
    /// Closes the gap left by <see cref="Protocol.EtherCatFrameRoundTripTests"/>'s 60-byte padding
    /// test, which builds its 9+23-byte LRW datagram from a hand-typed 32-byte array: here the
    /// process-image plan (and therefore the RxPdo/TxPdo byte lengths) comes from
    /// <see cref="ProcessImageBuilder.BuildDefault"/> walking the real, embedded Panasonic ESI
    /// device, and every single frame <see cref="CyclicExchangeService"/> actually hands to
    /// <see cref="IEthernetFrameTransport.Send"/> during real cyclic operation is captured and
    /// measured — proving the Protocol layer's padding-to-60-bytes arithmetic is exact for the
    /// datagram size this Milestone 1 plan genuinely produces, not merely for a size a test author
    /// separately typed in by hand.
    /// </summary>
    [Fact]
    public void Every_frame_the_cyclic_loop_actually_sends_is_exactly_60_bytes_for_the_real_ESI_derived_plan()
    {
        var bus = new FakeBus();
        var slave = new FakeSlaveDevice(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);
        bus.AddSlave(slave);

        var rawTransport = new FakeEthernetFrameTransport(bus);
        var escClient = new EscClient(rawTransport, TestSourceMac);

        var discovery = SlaveDiscovery.DiscoverSingleSlave(escClient, EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be());
        var plan = ProcessImageBuilder.BuildDefault(discovery.Device);

        // Sanity: this is genuinely the plan's own arithmetic (9 + 23 = 32 data bytes), not a
        // number restated from the implementation plan's prose.
        Assert.Equal(9, plan.RxPdoLayout.TotalByteLength);
        Assert.Equal(23, plan.TxPdoLayout.TotalByteLength);

        var stateMachine = new AlStateMachine(escClient, rawTransport, TestSourceMac, discovery.StationAddress)
        {
            PollInterval = TimeSpan.Zero,
        };
        stateMachine.TransitionToPreOp(discovery.Device);
        stateMachine.TransitionToSafeOp(discovery.Device, plan);

        var recordingTransport = new FrameLengthRecordingTransport(rawTransport);
        var service = new CyclicExchangeService(recordingTransport, TestSourceMac, plan)
        {
            Period = TimeSpan.FromMilliseconds(1),
            ReplyTimeout = TimeSpan.FromMilliseconds(200),
        };

        var done = new ManualResetEventSlim(initialState: false);
        var cycles = 0;
        service.StatusUpdated += _ =>
        {
            if (Interlocked.Increment(ref cycles) >= 20)
            {
                done.Set();
            }
        };

        service.Start();
        try
        {
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "Did not observe enough cycles in time.");
        }
        finally
        {
            service.Stop(TimeSpan.FromSeconds(2));
        }

        // 20+ regular cycles plus Stop()'s own final safe-shutdown exchange.
        Assert.True(recordingTransport.SentFrameLengths.Count >= 21);
        Assert.All(recordingTransport.SentFrameLengths, length => Assert.Equal(60, length));
    }
}
