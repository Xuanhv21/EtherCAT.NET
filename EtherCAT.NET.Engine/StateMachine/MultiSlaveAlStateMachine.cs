using System.Diagnostics;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.StateMachine;

/// <summary>
/// The multi-slave counterpart of <see cref="AlStateMachine"/>: drives a whole GROUP of already-
/// discovered, already-addressed slaves through INIT -&gt; PREOP -&gt; SAFEOP -&gt; OP together, so that
/// the group's single combined cyclic LRW exchange (see <see cref="ProcessData.MultiSlaveProcessImagePlan"/>
/// and <see cref="ProcessData.MultiSlaveCyclicExchangeService"/>) can be started once, covering every
/// slave at once, rather than per slave.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate class rather than a loop of <see cref="AlStateMachine"/> calls:</b> the
/// SAFEOP timing subtlety <see cref="AlStateMachine.TransitionToSafeOp"/> documents — fire
/// <c>onSafeOpRequested</c> immediately after AL Control is written, before polling for confirmation
/// — has to apply ACROSS THE WHOLE GROUP here, not per slave: every slave's FMMU/SM must be
/// configured and every slave's AL Control write for SAFEOP must have gone out *before* the single
/// combined cyclic exchange starts (it would otherwise immediately send LRW datagrams covering slaves
/// whose FMMUs/SMs are not configured yet). Looping single-slave <see cref="AlStateMachine"/> calls,
/// each firing the callback right after its own one AL Control write, cannot express that "fire once,
/// after everyone" ordering — so this class reimplements the same configure/request/poll shape
/// directly over <see cref="Esc.EscClient"/>, one slave at a time for the per-slave register writes,
/// but with the callback and the SAFEOP/OP confirmation polling applied to the whole group together.
/// </para>
/// <para>
/// Every failed or timed-out transition throws <see cref="MultiSlaveAlStateTransitionException"/>,
/// which always identifies WHICH slave (index and station address) failed, alongside the same
/// attempted/actual state and AL Status Code detail <see cref="AlStateTransitionException"/> carries
/// for the single-slave case.
/// </para>
/// </remarks>
public sealed class MultiSlaveAlStateMachine
{
    private readonly EscClient _escClient;
    private readonly IEthernetFrameTransport _transport;
    private readonly MacAddress _source;
    private readonly IReadOnlyList<ushort> _stationAddresses;
    private byte _nextLogicalExchangeIndex;

    /// <summary>Creates a <see cref="MultiSlaveAlStateMachine"/> for the slaves already addressed as <paramref name="stationAddresses"/>, in the same order the corresponding <see cref="ProcessData.MultiSlaveProcessImagePlan"/> was built in.</summary>
    /// <param name="escClient">Register-access client used for every SM/FMMU/AL Control/AL Status access.</param>
    /// <param name="transport">The same transport <paramref name="escClient"/> was built on. Used directly (bypassing <paramref name="escClient"/>) only for the minimal LRW readiness probe in <see cref="TransitionToOp"/>.</param>
    /// <param name="source">Source MAC address to stamp on the LRW probe frames built in <see cref="TransitionToOp"/>.</param>
    /// <param name="stationAddresses">Every slave's Configured Station Address, in group order.</param>
    public MultiSlaveAlStateMachine(EscClient escClient, IEthernetFrameTransport transport, MacAddress source, IReadOnlyList<ushort> stationAddresses)
    {
        ArgumentNullException.ThrowIfNull(escClient);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(stationAddresses);
        if (stationAddresses.Count == 0)
        {
            throw new ArgumentException("At least one slave is required.", nameof(stationAddresses));
        }

        _escClient = escClient;
        _transport = transport;
        _source = source;
        _stationAddresses = stationAddresses;
    }

    /// <summary>How long <see cref="TransitionToOp"/>'s LRW readiness probe waits for a reply to one exchange before treating it as failed. Defaults to 200 ms.</summary>
    public TimeSpan LogicalExchangeTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Interval between AL Status poll rounds (and between consecutive LRW readiness-probe exchanges). Defaults to 10 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long the WHOLE GROUP has to reach a requested AL state before giving up and throwing <see cref="MultiSlaveAlStateTransitionException"/> for whichever slave is still not there. Defaults to 2 seconds, shared across every slave (not per slave).</summary>
    public TimeSpan TransitionTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Number of consecutive good (expected-Working-Counter) LRW exchanges <see cref="TransitionToOp"/> requires before it requests OP for every slave. Defaults to 10.</summary>
    public int RequiredConsecutiveGoodExchanges { get; set; } = 10;

    /// <summary>Overall time budget for <see cref="TransitionToOp"/>'s readiness probe to accumulate <see cref="RequiredConsecutiveGoodExchanges"/> consecutive good exchanges before it gives up. Defaults to 5 seconds.</summary>
    public TimeSpan CyclicExchangeReadinessTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// INIT -&gt; PREOP for the whole group: writes SM0/SM1 verbatim from each slave's own matched
    /// device, in group order, then writes AL Control = PREOP to every slave, then polls every slave
    /// until all confirm PREOP.
    /// </summary>
    /// <param name="devices">Each slave's matched device descriptor, in the same order as the station addresses this instance was constructed with.</param>
    /// <exception cref="ArgumentException"><paramref name="devices"/>'s count does not match the number of slaves, or some device declares fewer than 2 SyncManagers.</exception>
    /// <exception cref="MultiSlaveAlStateTransitionException">Some slave refused the transition, or the group did not all reach PREOP within <see cref="TransitionTimeout"/>.</exception>
    public void TransitionToPreOp(IReadOnlyList<EsiDeviceDescriptor> devices)
    {
        RequireSameCount(devices);

        for (var i = 0; i < _stationAddresses.Count; i++)
        {
            RequireSyncManagerCount(devices[i], 2, i);
            _escClient.WriteSmConfig(_stationAddresses[i], 0, ToSmConfig(devices[i].SyncManagers[0]));
            _escClient.WriteSmConfig(_stationAddresses[i], 1, ToSmConfig(devices[i].SyncManagers[1]));
        }

        foreach (var stationAddress in _stationAddresses)
        {
            _escClient.WriteAlControl(stationAddress, AlState.PreOp);
        }

        AwaitStateAll(AlState.PreOp);
    }

    /// <summary>
    /// PREOP -&gt; SAFEOP for the whole group: writes FMMU0/FMMU1 (from <paramref name="plan"/>) and
    /// SM2/SM3 (verbatim from each slave's own matched device) for every slave, writes AL Control =
    /// SAFEOP to every slave, and only THEN — once every slave has had SAFEOP requested — invokes
    /// <paramref name="onSafeOpRequested"/> exactly once, before polling any slave for confirmation.
    /// See the type-level remarks for why the callback must fire after the whole group's writes, not
    /// per slave.
    /// </summary>
    /// <param name="devices">Each slave's matched device descriptor, in station-address order.</param>
    /// <param name="plan">The combined process-image plan (every slave's FMMU0/FMMU1) computed by <see cref="ProcessData.ProcessImageBuilder.BuildMulti"/>.</param>
    /// <param name="onSafeOpRequested">Optional hook invoked exactly once, synchronously, immediately after every slave's AL Control has been written to request SAFEOP and before this method starts polling any slave for confirmation — in production, <c>() =&gt; multiSlaveCyclicExchangeService.Start()</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="devices"/>'s count does not match the number of slaves, <paramref name="plan"/>'s slave count does not match, or some device declares fewer than 4 SyncManagers.</exception>
    /// <exception cref="MultiSlaveAlStateTransitionException">Some slave refused the transition, or the group did not all reach SAFEOP within <see cref="TransitionTimeout"/>.</exception>
    public void TransitionToSafeOp(IReadOnlyList<EsiDeviceDescriptor> devices, MultiSlaveProcessImagePlan plan, Action? onSafeOpRequested = null)
    {
        RequireSameCount(devices);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Slaves.Count != _stationAddresses.Count)
        {
            throw new ArgumentException(
                $"Plan describes {plan.Slaves.Count} slave(s) but this state machine was constructed for {_stationAddresses.Count}.",
                nameof(plan));
        }

        for (var i = 0; i < _stationAddresses.Count; i++)
        {
            RequireSyncManagerCount(devices[i], 4, i);

            _escClient.WriteFmmuConfig(_stationAddresses[i], 0, plan.Slaves[i].OutputsFmmu);
            _escClient.WriteFmmuConfig(_stationAddresses[i], 1, plan.Slaves[i].InputsFmmu);
            _escClient.WriteSmConfig(_stationAddresses[i], 2, ToSmConfig(devices[i].SyncManagers[2]));
            _escClient.WriteSmConfig(_stationAddresses[i], 3, ToSmConfig(devices[i].SyncManagers[3]));
        }

        foreach (var stationAddress in _stationAddresses)
        {
            _escClient.WriteAlControl(stationAddress, AlState.SafeOp);
        }

        // Fire ONCE, only after every slave in the group has had SAFEOP requested -- see the
        // type-level remarks for why this cannot be done per slave.
        onSafeOpRequested?.Invoke();

        AwaitStateAll(AlState.SafeOp);
    }

    /// <summary>
    /// SAFEOP -&gt; OP for the whole group: waits for <see cref="RequiredConsecutiveGoodExchanges"/>
    /// consecutive LRW exchanges against the group's combined logical process image (expecting
    /// <see cref="MultiSlaveProcessImagePlan.ExpectedWorkingCounter"/>, i.e. 2 per slave) to come back
    /// clean, then writes AL Control = OP to every slave and polls until all confirm OP.
    /// </summary>
    /// <exception cref="EscCommunicationException"><see cref="RequiredConsecutiveGoodExchanges"/> consecutive good exchanges were not observed within <see cref="CyclicExchangeReadinessTimeout"/>.</exception>
    /// <exception cref="MultiSlaveAlStateTransitionException">Some slave refused the transition, or the group did not all reach OP within <see cref="TransitionTimeout"/>.</exception>
    public void TransitionToOp(MultiSlaveProcessImagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        WaitForConsecutiveGoodExchanges(plan);

        foreach (var stationAddress in _stationAddresses)
        {
            _escClient.WriteAlControl(stationAddress, AlState.Op);
        }

        AwaitStateAll(AlState.Op);
    }

    /// <summary>Runs the full INIT -&gt; PREOP -&gt; SAFEOP -&gt; OP sequence for the whole group in one call.</summary>
    public void BringUpToOp(IReadOnlyList<EsiDeviceDescriptor> devices, MultiSlaveProcessImagePlan plan, Action? onSafeOpRequested = null)
    {
        TransitionToPreOp(devices);
        TransitionToSafeOp(devices, plan, onSafeOpRequested);
        TransitionToOp(plan);
    }

    private void WaitForConsecutiveGoodExchanges(MultiSlaveProcessImagePlan plan)
    {
        var expectedWorkingCounter = plan.ExpectedWorkingCounter;
        var buffer = new byte[plan.TotalLength];

        var consecutiveGood = 0;
        var elapsed = Stopwatch.StartNew();

        while (consecutiveGood < RequiredConsecutiveGoodExchanges)
        {
            if (elapsed.Elapsed >= CyclicExchangeReadinessTimeout)
            {
                throw new EscCommunicationException(
                    $"Gave up waiting for {RequiredConsecutiveGoodExchanges} consecutive good LRW exchanges " +
                    $"(WKC={expectedWorkingCounter}, {plan.Slaves.Count} slave(s)) after {elapsed.Elapsed.TotalMilliseconds:F0} ms; " +
                    $"reached {consecutiveGood} in a row before the streak broke.");
            }

            var wkc = PerformLogicalExchange(buffer, logicalAddress: 0);
            consecutiveGood = wkc == expectedWorkingCounter ? consecutiveGood + 1 : 0;

            if (consecutiveGood < RequiredConsecutiveGoodExchanges && PollInterval > TimeSpan.Zero)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    /// <summary>
    /// Sends one LRW datagram covering <paramref name="data"/>.Length bytes starting at
    /// <paramref name="logicalAddress"/> and returns the Working Counter of the reply, or throws if
    /// no reply is observed within <see cref="LogicalExchangeTimeout"/>. Built directly on
    /// <see cref="Protocol"/> + <see cref="IEthernetFrameTransport"/>, deliberately not going through
    /// <see cref="EscClient"/> -- mirrors the reply-matching pattern of <see cref="AlStateMachine"/>'s
    /// own equivalent private helper.
    /// </summary>
    private ushort PerformLogicalExchange(byte[] data, uint logicalAddress)
    {
        var index = _nextLogicalExchangeIndex++;
        var request = new EtherCatDatagram(EtherCatCommand.Lrw, index, EtherCatAddress.ForLogicalAddressed(logicalAddress), data);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, _source, [request]);

        EtherCatDatagram? reply = null;
        using var replyReceived = new ManualResetEventSlim(initialState: false);

        void OnFrameReceived(object? sender, ReadOnlyMemory<byte> rawFrame)
        {
            EthernetFrame parsed;
            try
            {
                parsed = EtherCatFrameParser.Parse(rawFrame.Span);
            }
            catch
            {
                return;
            }

            foreach (var candidate in parsed.Datagrams)
            {
                if (candidate.Command == EtherCatCommand.Lrw && candidate.Index == index)
                {
                    reply = candidate;
                    replyReceived.Set();
                    return;
                }
            }
        }

        _transport.FrameReceived += OnFrameReceived;
        try
        {
            _transport.Send(frame);

            if (!replyReceived.Wait(LogicalExchangeTimeout))
            {
                throw new EscCommunicationException(
                    $"Timed out after {LogicalExchangeTimeout.TotalMilliseconds:F0} ms waiting for an LRW reply " +
                    "during the group's SAFEOP->OP readiness check.");
            }
        }
        finally
        {
            _transport.FrameReceived -= OnFrameReceived;
        }

        return reply?.WorkingCounter ?? 0;
    }

    private void AwaitStateAll(AlState expected)
    {
        var confirmed = new bool[_stationAddresses.Count];
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            var allConfirmed = true;

            for (var i = 0; i < _stationAddresses.Count; i++)
            {
                if (confirmed[i])
                {
                    continue;
                }

                var report = _escClient.ReadAlStatus(_stationAddresses[i]);

                if (!report.HasError && report.State == expected)
                {
                    confirmed[i] = true;
                    continue;
                }

                allConfirmed = false;

                if (report.HasError)
                {
                    throw new MultiSlaveAlStateTransitionException(i, _stationAddresses[i], expected, report.State, report.StatusCode, timedOut: false);
                }

                if (elapsed.Elapsed >= TransitionTimeout)
                {
                    throw new MultiSlaveAlStateTransitionException(i, _stationAddresses[i], expected, report.State, report.StatusCode, timedOut: true);
                }
            }

            if (allConfirmed)
            {
                return;
            }

            if (PollInterval > TimeSpan.Zero)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    private void RequireSameCount(IReadOnlyList<EsiDeviceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count != _stationAddresses.Count)
        {
            throw new ArgumentException(
                $"{devices.Count} device(s) supplied but this state machine was constructed for {_stationAddresses.Count} slave(s).",
                nameof(devices));
        }
    }

    private void RequireSyncManagerCount(EsiDeviceDescriptor device, int required, int slaveIndex)
    {
        if (device.SyncManagers.Count < required)
        {
            throw new ArgumentException(
                $"Slave {slaveIndex} (station 0x{_stationAddresses[slaveIndex]:X4}), device '{device.Name}', declares only " +
                $"{device.SyncManagers.Count} SyncManager(s); at least {required} are required.",
                nameof(device));
        }
    }

    private static SmConfig ToSmConfig(EsiSyncManager sm) =>
        new(sm.StartAddress, sm.DefaultSize, sm.ControlByte, Enable: sm.Enable);
}
