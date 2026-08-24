using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.StateMachine;

/// <summary>
/// Drives one already-discovered, already-addressed slave through the mandatory
/// INIT -&gt; PREOP -&gt; SAFEOP -&gt; OP AL state sequence described in the implementation plan's
/// "State machine" section, using <see cref="EscClient"/> for every register access (SM/FMMU
/// configuration, AL Control writes, AL Status/Status Code polling).
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing subtlety (SAFEOP):</b> <see cref="TransitionToSafeOp"/> configures FMMU0/FMMU1 and
/// SM2/SM3, writes AL Control = SAFEOP, and only <i>then</i> invokes <c>onSafeOpRequested</c> —
/// immediately, before polling AL Status for confirmation. This is deliberate: the ESC's Sync
/// Manager watchdog starts counting the instant SM2/SM3 (the process-data Sync Managers) are
/// enabled, which happens as a side effect of the slave accepting the SAFEOP request — not once
/// AL Status confirms SAFEOP was reached. A caller that waited for confirmation before starting the
/// cyclic LRW exchange risks the watchdog tripping before the first output ever arrives. This class
/// deliberately does not own that exchange loop itself (a later milestone step's
/// <c>CyclicExchangeService</c> does, with its own 10ms-cadence background thread, mirroring, and
/// Statusword decode) — <c>onSafeOpRequested</c> is the hook that lets that component start itself
/// at exactly the right instant; in production it is wired to something like
/// <c>() =&gt; cyclicExchangeService.Start()</c>. See <see cref="TransitionToSafeOp"/> for the exact
/// callback shape.
/// </para>
/// <para>
/// <b>OP gating:</b> Per the plan, the master must not request OP until it has observed N
/// consecutive cyclic exchanges with the Working Counter the process-data FMMUs imply (2, for this
/// single-slave/one-write-FMMU/one-read-FMMU plan) — proof that the logical process image path
/// (LRW through FMMU0/FMMU1) is actually alive, not just that AL Status says SAFEOP.
/// <see cref="TransitionToOp"/> performs this gating with its own minimal LRW exchange, built
/// directly on <see cref="Protocol"/> + <see cref="IEthernetFrameTransport"/> (bypassing
/// <see cref="EscClient"/>, which by design does not expose logical addressing — see its remarks).
/// This is intentionally a separate, lightweight "is the path alive" probe, not a replacement for
/// the application-level cyclic loop <c>onSafeOpRequested</c> starts; both may be sending LRW
/// datagrams concurrently for the brief SAFEOP-&gt;OP bring-up window, which is harmless.
/// </para>
/// <para>
/// Every failed or timed-out transition throws <see cref="AlStateTransitionException"/>, which
/// always carries the AL state that was attempted, the AL state the slave actually stayed at, and
/// the AL Status Code (0x0134) read at the moment of failure — never a bare timeout.
/// </para>
/// </remarks>
public sealed class AlStateMachine
{
    private readonly EscClient _escClient;
    private readonly IEthernetFrameTransport _transport;
    private readonly MacAddress _source;
    private readonly ushort _stationAddress;
    private byte _nextLogicalExchangeIndex;

    /// <summary>Creates an <see cref="AlStateMachine"/> for the slave already addressed as <paramref name="stationAddress"/>.</summary>
    /// <param name="escClient">Register-access client used for every SM/FMMU/AL Control/AL Status access.</param>
    /// <param name="transport">
    /// The same transport <paramref name="escClient"/> was built on. Used directly (bypassing
    /// <paramref name="escClient"/>) only for the minimal LRW readiness probe in
    /// <see cref="TransitionToOp"/> — see the type-level remarks.
    /// </param>
    /// <param name="source">Source MAC address to stamp on the LRW probe frames built in <see cref="TransitionToOp"/>.</param>
    /// <param name="stationAddress">The slave's Configured Station Address (as assigned during discovery).</param>
    public AlStateMachine(EscClient escClient, IEthernetFrameTransport transport, MacAddress source, ushort stationAddress)
    {
        ArgumentNullException.ThrowIfNull(escClient);
        ArgumentNullException.ThrowIfNull(transport);

        _escClient = escClient;
        _transport = transport;
        _source = source;
        _stationAddress = stationAddress;
    }

    /// <summary>How long <see cref="TransitionToOp"/>'s LRW readiness probe waits for a reply to one exchange before treating it as failed. Defaults to 200 ms.</summary>
    public TimeSpan LogicalExchangeTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Interval between AL Status polls (and between consecutive LRW readiness-probe exchanges). Defaults to 10 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long any single AL state transition (<see cref="TransitionToPreOp"/>/<see cref="TransitionToSafeOp"/>/<see cref="TransitionToOp"/>) polls AL Status before giving up and throwing <see cref="AlStateTransitionException"/>. Defaults to 2 seconds.</summary>
    public TimeSpan TransitionTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Number of consecutive good (expected-Working-Counter) LRW exchanges <see cref="TransitionToOp"/>
    /// requires before it requests OP. Configurable per the implementation plan; defaults to 10.
    /// </summary>
    public int RequiredConsecutiveGoodExchanges { get; set; } = 10;

    /// <summary>
    /// Overall time budget for <see cref="TransitionToOp"/>'s readiness probe to accumulate
    /// <see cref="RequiredConsecutiveGoodExchanges"/> consecutive good exchanges before it gives up.
    /// Defaults to 5 seconds.
    /// </summary>
    public TimeSpan CyclicExchangeReadinessTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// INIT -&gt; PREOP: writes SM0 (MBoxOut) and SM1 (MBoxIn) verbatim from
    /// <paramref name="device"/>'s ESI-declared SyncManagers (their <c>ControlByte</c> is used
    /// exactly as parsed — e.g. 0x26/0x22 for this plan's device — never recomputed), then requests
    /// AL state PREOP and polls AL Status until the slave confirms it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="device"/> declares fewer than 2 SyncManagers.</exception>
    /// <exception cref="AlStateTransitionException">The slave refused the transition, or never reached PREOP within <see cref="TransitionTimeout"/>.</exception>
    public AlStatusReport TransitionToPreOp(EsiDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        RequireSyncManagerCount(device, 2);

        _escClient.WriteSmConfig(_stationAddress, 0, ToSmConfig(device.SyncManagers[0]));
        _escClient.WriteSmConfig(_stationAddress, 1, ToSmConfig(device.SyncManagers[1]));

        _escClient.WriteAlControl(_stationAddress, AlState.PreOp);
        return AwaitState(AlState.PreOp);
    }

    /// <summary>
    /// PREOP -&gt; SAFEOP: writes FMMU0/FMMU1 from <paramref name="plan"/> and SM2/SM3 verbatim from
    /// <paramref name="device"/>'s ESI-declared SyncManagers (ControlByte 0x64/0x20 for this plan's
    /// device), requests AL state SAFEOP, then — immediately, before polling AL Status for
    /// confirmation — invokes <paramref name="onSafeOpRequested"/> if supplied. See the type-level
    /// remarks for why that ordering matters and what the callback is for.
    /// </summary>
    /// <param name="device">The matched device descriptor; must declare at least 4 SyncManagers (SM0..SM3).</param>
    /// <param name="plan">The process-image plan (FMMU0/FMMU1) computed by <see cref="ProcessImageBuilder"/> for this device.</param>
    /// <param name="onSafeOpRequested">
    /// Optional hook invoked exactly once, synchronously, immediately after AL Control has been
    /// written to request SAFEOP and before this method starts polling AL Status for confirmation.
    /// Takes no parameters and returns nothing — its sole contract is "the SM watchdog may now be
    /// counting; start whatever needs to be feeding process data". In production this is the point
    /// at which the cyclic-exchange step's background loop must be started (e.g.
    /// <c>() =&gt; cyclicExchangeService.Start()</c>); tests may pass a delegate that just records that
    /// it was called, or <c>null</c> to skip it entirely.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="device"/> declares fewer than 4 SyncManagers.</exception>
    /// <exception cref="AlStateTransitionException">The slave refused the transition, or never reached SAFEOP within <see cref="TransitionTimeout"/>.</exception>
    public AlStatusReport TransitionToSafeOp(EsiDeviceDescriptor device, ProcessImagePlan plan, Action? onSafeOpRequested = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(plan);
        RequireSyncManagerCount(device, 4);

        _escClient.WriteFmmuConfig(_stationAddress, 0, plan.OutputsFmmu);
        _escClient.WriteFmmuConfig(_stationAddress, 1, plan.InputsFmmu);
        _escClient.WriteSmConfig(_stationAddress, 2, ToSmConfig(device.SyncManagers[2]));
        _escClient.WriteSmConfig(_stationAddress, 3, ToSmConfig(device.SyncManagers[3]));

        _escClient.WriteAlControl(_stationAddress, AlState.SafeOp);

        // Critical timing subtlety -- see the type-level remarks: this must fire right here, before
        // the AL Status poll loop below, not after SAFEOP is confirmed.
        onSafeOpRequested?.Invoke();

        return AwaitState(AlState.SafeOp);
    }

    /// <summary>
    /// SAFEOP -&gt; OP: waits for <see cref="RequiredConsecutiveGoodExchanges"/> consecutive LRW
    /// exchanges against the logical process image described by <paramref name="plan"/> to come back
    /// with the expected Working Counter (2: one write-enabled FMMU + one read-enabled FMMU on this
    /// single slave), then requests AL state OP and polls AL Status until the slave confirms it. See
    /// the type-level remarks for why this method performs its own minimal LRW exchange rather than
    /// depending on the cyclic-exchange step's loop.
    /// </summary>
    /// <exception cref="EscCommunicationException">
    /// <see cref="RequiredConsecutiveGoodExchanges"/> consecutive good exchanges were not observed
    /// within <see cref="CyclicExchangeReadinessTimeout"/>.
    /// </exception>
    /// <exception cref="AlStateTransitionException">The slave refused the transition, or never reached OP within <see cref="TransitionTimeout"/>.</exception>
    public AlStatusReport TransitionToOp(ProcessImagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        WaitForConsecutiveGoodExchanges(plan);

        _escClient.WriteAlControl(_stationAddress, AlState.Op);
        return AwaitState(AlState.Op);
    }

    /// <summary>
    /// Runs the full INIT -&gt; PREOP -&gt; SAFEOP -&gt; OP sequence for one slave in one call:
    /// <see cref="TransitionToPreOp"/>, then <see cref="TransitionToSafeOp"/> (with
    /// <paramref name="onSafeOpRequested"/> wired through), then <see cref="TransitionToOp"/>.
    /// </summary>
    public AlStatusReport BringUpToOp(EsiDeviceDescriptor device, ProcessImagePlan plan, Action? onSafeOpRequested = null)
    {
        TransitionToPreOp(device);
        TransitionToSafeOp(device, plan, onSafeOpRequested);
        return TransitionToOp(plan);
    }

    private void WaitForConsecutiveGoodExchanges(ProcessImagePlan plan)
    {
        const ushort expectedWorkingCounter = 2;

        var totalLength = plan.RxPdoLayout.TotalByteLength + plan.TxPdoLayout.TotalByteLength;
        var buffer = new byte[totalLength];

        var consecutiveGood = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (consecutiveGood < RequiredConsecutiveGoodExchanges)
        {
            if (elapsed.Elapsed >= CyclicExchangeReadinessTimeout)
            {
                throw new EscCommunicationException(
                    $"Gave up waiting for {RequiredConsecutiveGoodExchanges} consecutive good LRW exchanges " +
                    $"(WKC={expectedWorkingCounter}) after {elapsed.Elapsed.TotalMilliseconds:F0} ms; " +
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
    /// <see cref="Protocol"/> + <see cref="IEthernetFrameTransport"/>, deliberately not going
    /// through <see cref="EscClient"/> (which does not expose logical addressing) -- mirrors the
    /// reply-matching pattern of <c>EscClient</c>'s own private exchange helper.
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
                    "during the SAFEOP->OP readiness check.");
            }
        }
        finally
        {
            _transport.FrameReceived -= OnFrameReceived;
        }

        return reply?.WorkingCounter ?? 0;
    }

    private AlStatusReport AwaitState(AlState expected)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var report = _escClient.ReadAlStatus(_stationAddress);

            if (!report.HasError && report.State == expected)
            {
                return report;
            }

            if (report.HasError)
            {
                throw new AlStateTransitionException(expected, report.State, report.StatusCode, timedOut: false);
            }

            if (elapsed.Elapsed >= TransitionTimeout)
            {
                throw new AlStateTransitionException(expected, report.State, report.StatusCode, timedOut: true);
            }

            if (PollInterval > TimeSpan.Zero)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    private static void RequireSyncManagerCount(EsiDeviceDescriptor device, int required)
    {
        if (device.SyncManagers.Count < required)
        {
            throw new ArgumentException(
                $"Device '{device.Name}' declares only {device.SyncManagers.Count} SyncManager(s); at least {required} are required.",
                nameof(device));
        }
    }

    private static SmConfig ToSmConfig(EsiSyncManager sm) =>
        new(sm.StartAddress, sm.DefaultSize, sm.ControlByte, Enable: sm.Enable);
}
