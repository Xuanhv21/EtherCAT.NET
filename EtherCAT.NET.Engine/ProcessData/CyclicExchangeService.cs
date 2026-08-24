using System.Diagnostics;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// The Milestone 1 cyclic process-data exchange loop described in the implementation plan's
/// "Cyclic process-data exchange" and "Threading &amp; vong lap tuan hoan" sections: a dedicated
/// background thread that, every <see cref="Period"/> (default 10 ms, paced by a
/// <see cref="Stopwatch"/> with drift correction rather than a naive fixed sleep), builds one LRW
/// datagram covering the logical range <see cref="ProcessImagePlan.OutputsFmmu"/> +
/// <see cref="ProcessImagePlan.InputsFmmu"/> describe, sends it, and decodes the reply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety invariant (structural, not conventional):</b> every single cycle — including the very
/// first one, before <see cref="SetControlword"/> has ever been called, and including the final
/// safe-shutdown cycle <see cref="Stop"/> triggers — this class writes <c>ModesOfOperation = 0</c>
/// and <c>TargetPosition = </c> the <c>PositionActualValue</c> read back on the immediately
/// preceding successful cycle, unconditionally, before the outbound datagram is ever sent. There is
/// no constructor parameter, property, method, or overload anywhere on this class that can set
/// either field to anything else — the only way those two fields ever take on a different value is
/// by the slave itself reporting a different <c>PositionActualValue</c> next cycle. The only
/// externally reachable control surface for driving the DS402 power state is
/// <see cref="SetControlword"/>.
/// </para>
/// <para>
/// This service does not drive the AL state machine and does not read the AL Status register
/// itself (that would add register traffic outside the LRW exchange this class owns); a caller
/// (typically <see cref="StateMachine.AlStateMachine"/>'s <c>onSafeOpRequested</c> hook to start
/// this service, and application code once OP is confirmed) informs it of the current AL state via
/// <see cref="SetAlState"/> purely for display in <see cref="ProcessImageSnapshot.AlState"/>.
/// </para>
/// </remarks>
public sealed class CyclicExchangeService : IDisposable
{
    /// <summary>The Working Counter a healthy cycle must return: one write-enabled FMMU (outputs) + one read-enabled FMMU (inputs) on this single-slave plan.</summary>
    private const ushort ExpectedWorkingCounter = 2;

    private readonly IEthernetFrameTransport _transport;
    private readonly MacAddress _source;
    private readonly int _rxLength;
    private readonly int _txLength;
    private readonly PdoLayout _rxLayout;
    private readonly PdoLayout _txLayout;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _started;

    private volatile ushort _pendingControlword = Ds402Controlword.DisableVoltage;
    private AlState _alState = AlState.SafeOp;

    private byte _nextDatagramIndex;
    private long _sequenceCounter;

    // Mutated only on the cyclic thread; safe to read from StatusUpdated (also invoked on that thread).
    private int _lastPositionActualValue;
    private ushort _lastRawStatusword;
    private bool _lastFaultBit;
    private int _consecutiveFailures;
    private ushort _lastWkc;
    private bool _isDataFresh;
    private string? _lastError;

    /// <summary>Creates a service that exchanges one LRW datagram per cycle over <paramref name="transport"/>, covering the logical range described by <paramref name="plan"/>.</summary>
    /// <param name="transport">The transport to send LRW datagrams on and receive replies from.</param>
    /// <param name="source">Source MAC address to stamp on every outgoing frame.</param>
    /// <param name="plan">The process-image plan (FMMU0/FMMU1 + RxPdo/TxPdo layouts) computed by <see cref="ProcessImageBuilder"/> for the slave to exchange with.</param>
    public CyclicExchangeService(IEthernetFrameTransport transport, MacAddress source, ProcessImagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(plan);

        _transport = transport;
        _source = source;
        _rxLayout = plan.RxPdoLayout;
        _txLayout = plan.TxPdoLayout;
        _rxLength = plan.RxPdoLayout.TotalByteLength;
        _txLength = plan.TxPdoLayout.TotalByteLength;
    }

    /// <summary>Cycle period. Paced by a <see cref="Stopwatch"/> with drift correction (measure elapsed, sleep the clamped remainder), never a fixed <c>Thread.Sleep</c>. Defaults to 10 ms.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long one cycle's LRW exchange waits for a reply before that cycle counts as failed. Defaults to 5 ms (well under <see cref="Period"/>; irrelevant against a synchronous fake transport, which replies before <c>Send</c> returns).</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Number of consecutive failed cycles (WKC mismatch or no reply) that stops the loop and raises <see cref="Faulted"/>. Defaults to 10.</summary>
    public int MaxConsecutiveFailures { get; set; } = 10;

    /// <summary><c>true</c> from <see cref="Start"/> until the background thread actually exits.</summary>
    public bool IsRunning { get; private set; }

    /// <summary><c>true</c> once <see cref="MaxConsecutiveFailures"/> consecutive failed cycles have stopped the loop.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>The AL state last reported via <see cref="SetAlState"/>; this service never reads the AL Status register itself. Defaults to <see cref="AlState.SafeOp"/>, matching the point in the bring-up sequence at which this service is meant to be started.</summary>
    public AlState AlState => _alState;

    /// <summary>Raised at the end of every cycle (success or failure) with a fresh snapshot of observable state.</summary>
    public event Action<ProcessImageSnapshot>? StatusUpdated;

    /// <summary>Raised only on meaningful state changes — AL state change, WKC mismatch/missing reply, a Fault bit 0-&gt;1 transition, or the final fault/stop message — never once per tick.</summary>
    public event Action<string>? LogEmitted;

    /// <summary>Raised exactly once, from the cyclic thread, the moment <see cref="MaxConsecutiveFailures"/> consecutive failed cycles are observed and the loop stops itself.</summary>
    public event Action<string>? Faulted;

    /// <summary>
    /// Informs this service of the AL state to report in <see cref="ProcessImageSnapshot.AlState"/>
    /// going forward (e.g. call with <see cref="AlState.Op"/> once <see cref="StateMachine.AlStateMachine.TransitionToOp"/>
    /// confirms OP). Logs the transition via <see cref="LogEmitted"/> when it actually changes.
    /// </summary>
    public void SetAlState(AlState state)
    {
        var previous = _alState;
        _alState = state;
        if (previous != state)
        {
            LogEmitted?.Invoke($"AL state changed: {previous} -> {state}.");
        }
    }

    /// <summary>
    /// Enqueues <paramref name="controlword"/> to be written into the outbound Controlword field on
    /// the next cycle (and every cycle after, until changed again). This is the sole way anything
    /// outside this class can influence the DS402 power state — it never drives Controlword on its
    /// own initiative.
    /// </summary>
    public void SetControlword(ushort controlword) => _pendingControlword = controlword;

    /// <summary>
    /// Starts the dedicated background thread (<see cref="ThreadPriority.Normal"/>, <c>IsBackground = true</c>).
    /// Intended to be wired as an <see cref="StateMachine.AlStateMachine.TransitionToSafeOp"/>
    /// <c>onSafeOpRequested</c> callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException($"{nameof(CyclicExchangeService)} has already been started.");
        }

        IsRunning = true;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "EtherCAT.CyclicExchange",
        };
        _thread.Start();
    }

    /// <summary>
    /// Requests the loop stop. The loop's own thread — never the caller's — then attempts one final
    /// LRW exchange with the Controlword forced to <see cref="Ds402Controlword.DisableVoltage"/>
    /// (best-effort; failures are logged, not thrown) before exiting, after which this method joins
    /// the thread and returns. A no-op if never started.
    /// </summary>
    /// <param name="joinTimeout">How long to wait for the thread to exit. Defaults to 2 seconds.</param>
    public void Stop(TimeSpan? joinTimeout = null)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }

        _stopRequested = true;
        _thread?.Join(joinTimeout ?? TimeSpan.FromSeconds(2));
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void RunLoop()
    {
        var stopwatch = new Stopwatch();

        try
        {
            while (!_stopRequested && !IsFaulted)
            {
                stopwatch.Restart();

                RunCycle(forcedControlword: null);

                if (_stopRequested || IsFaulted)
                {
                    break;
                }

                var remaining = Period - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }
            }
        }
        finally
        {
            try
            {
                RunCycle(forcedControlword: Ds402Controlword.DisableVoltage);
            }
            catch (Exception ex)
            {
                LogEmitted?.Invoke($"Final safe-shutdown exchange threw: {ex.Message}");
            }

            IsRunning = false;
        }
    }

    /// <summary>
    /// Runs exactly one LRW exchange cycle: builds the outbound datagram (mirroring
    /// PositionActualValue into TargetPosition and holding ModesOfOperation at 0, unconditionally),
    /// sends it, decodes the reply (or records the failure), and raises <see cref="StatusUpdated"/>.
    /// </summary>
    private void RunCycle(ushort? forcedControlword)
    {
        var outbound = new byte[_rxLength + _txLength];
        var rx = new RxPdoImage(_rxLayout, outbound);

        // --- Safety invariant: unconditional, every cycle, no public API can change this. ---
        rx.ModesOfOperation = 0;
        rx.TargetPosition = _lastPositionActualValue;
        // ---------------------------------------------------------------------------------------

        rx.Controlword = forcedControlword ?? _pendingControlword;

        var (wkc, replyData) = PerformLogicalExchange(outbound);
        var sequence = Interlocked.Increment(ref _sequenceCounter);

        var ok = replyData is not null && wkc == ExpectedWorkingCounter;

        if (ok)
        {
            var txBuffer = new byte[_txLength];
            Array.Copy(replyData!, _rxLength, txBuffer, 0, _txLength);
            var tx = new TxPdoImage(_txLayout, txBuffer);

            _lastPositionActualValue = tx.PositionActualValue;
            _lastRawStatusword = tx.Statusword;
            _lastWkc = wkc;
            _isDataFresh = true;
            _lastError = null;
            _consecutiveFailures = 0;

            var status = new Ds402Statusword(_lastRawStatusword);
            if (status.Fault && !_lastFaultBit)
            {
                LogEmitted?.Invoke($"Statusword Fault bit transitioned 0->1 (Statusword=0x{_lastRawStatusword:X4}).");
            }

            _lastFaultBit = status.Fault;
        }
        else
        {
            _consecutiveFailures++;
            _lastWkc = wkc;
            _isDataFresh = false;
            _lastError = replyData is null
                ? $"No LRW reply observed within {ReplyTimeout.TotalMilliseconds:F0} ms."
                : $"LRW WKC mismatch: expected {ExpectedWorkingCounter}, got {wkc}.";

            LogEmitted?.Invoke(_lastError);
        }

        if (!ok && !IsFaulted && _consecutiveFailures >= MaxConsecutiveFailures)
        {
            IsFaulted = true;
            var message = $"{nameof(CyclicExchangeService)} faulted: {_consecutiveFailures} consecutive failed cycles (last error: {_lastError}).";
            LogEmitted?.Invoke(message);
            Faulted?.Invoke(message);
        }

        var snapshot = new ProcessImageSnapshot(
            sequence,
            _lastRawStatusword,
            new Ds402Statusword(_lastRawStatusword),
            _alState,
            _lastWkc,
            _isDataFresh,
            _lastError,
            IsFaulted);

        StatusUpdated?.Invoke(snapshot);
    }

    /// <summary>
    /// Sends one LRW datagram covering <paramref name="outboundData"/>.Length bytes starting at
    /// logical address 0 and waits for the matching reply. Built directly on <see cref="Protocol"/>
    /// + <see cref="IEthernetFrameTransport"/>, mirroring the reply-matching pattern of
    /// <see cref="Esc.EscClient"/>'s and <see cref="StateMachine.AlStateMachine"/>'s own exchange
    /// helpers.
    /// </summary>
    /// <returns>The reply's Working Counter and data, or <c>(0, null)</c> if no reply arrived within <see cref="ReplyTimeout"/>.</returns>
    private (ushort Wkc, byte[]? Data) PerformLogicalExchange(byte[] outboundData)
    {
        var index = _nextDatagramIndex++;
        var request = new EtherCatDatagram(EtherCatCommand.Lrw, index, EtherCatAddress.ForLogicalAddressed(0), outboundData);
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

            if (!replyReceived.Wait(ReplyTimeout))
            {
                return (0, null);
            }
        }
        finally
        {
            _transport.FrameReceived -= OnFrameReceived;
        }

        return reply is null ? (0, null) : (reply.WorkingCounter, reply.Data);
    }
}
