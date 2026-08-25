using System.Diagnostics;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// One slave's worth of observable state within a <see cref="MultiSlaveProcessImageSnapshot"/>.
/// </summary>
/// <param name="StationAddress">This slave's Configured Station Address.</param>
/// <param name="RawStatusword">This slave's Statusword (0x6041) from the most recent cycle whose data was fresh (see <see cref="MultiSlaveProcessImageSnapshot.IsDataFresh"/>).</param>
/// <param name="Status">Bit-decoded view of <paramref name="RawStatusword"/>.</param>
/// <param name="PositionActualValue">This slave's Position actual value (0x6064) from the most recent fresh cycle.</param>
public sealed record SlaveProcessImageSnapshot(
    ushort StationAddress,
    ushort RawStatusword,
    Ds402Statusword Status,
    int PositionActualValue);

/// <summary>
/// One cycle's worth of observable state for a whole group, published by
/// <see cref="MultiSlaveCyclicExchangeService.StatusUpdated"/> — the multi-slave counterpart of
/// <see cref="ProcessImageSnapshot"/>.
/// </summary>
/// <param name="SequenceNumber">Monotonically increasing per-cycle counter, starting at 1 on the first cycle.</param>
/// <param name="AlState">The AL state last reported to this service via <see cref="MultiSlaveCyclicExchangeService.SetAlState"/>.</param>
/// <param name="LastWkc">The Working Counter returned by this cycle's LRW exchange (0 if no reply was observed at all).</param>
/// <param name="IsDataFresh"><c>false</c> when this cycle's LRW exchange failed (WKC mismatch or no reply) — every slave's fields below then reflect the last cycle that did succeed, not this one.</param>
/// <param name="LastError">Human-readable description of the most recent failure, or <c>null</c> while healthy.</param>
/// <param name="IsFaulted"><c>true</c> once consecutive failures exceeded <see cref="MultiSlaveCyclicExchangeService.MaxConsecutiveFailures"/> and the loop has stopped itself.</param>
/// <param name="Slaves">Every slave's own decoded state, in the same order the service was constructed with.</param>
public sealed record MultiSlaveProcessImageSnapshot(
    long SequenceNumber,
    AlState AlState,
    ushort LastWkc,
    bool IsDataFresh,
    string? LastError,
    bool IsFaulted,
    IReadOnlyList<SlaveProcessImageSnapshot> Slaves);

/// <summary>
/// The multi-slave counterpart of <see cref="CyclicExchangeService"/>: one dedicated background
/// thread exchanging a single LRW datagram per cycle that covers a whole GROUP of slaves at once (per
/// <see cref="MultiSlaveProcessImagePlan"/>), rather than just one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safety invariant (structural, not conventional), per slave:</b> exactly like
/// <see cref="CyclicExchangeService"/>, every single cycle — for every slave in the group,
/// independently — this class writes that slave's <c>ModesOfOperation = 0</c> and
/// <c>TargetPosition = </c> that same slave's own <c>PositionActualValue</c> read back on the
/// immediately preceding successful cycle, unconditionally, before the outbound datagram is ever
/// sent. There is no public API that can set either field to anything else for any slave — the only
/// externally reachable control surface for driving any slave's DS402 power state is
/// <see cref="SetControlword"/>, which always targets exactly one slave by index.
/// </para>
/// <para>
/// This service does not drive any slave's AL state machine and does not read any AL Status register
/// itself; a caller (typically <see cref="StateMachine.MultiSlaveAlStateMachine"/>'s
/// <c>onSafeOpRequested</c> hook to start this service) informs it of the current AL state via
/// <see cref="SetAlState"/> purely for display.
/// </para>
/// </remarks>
public sealed class MultiSlaveCyclicExchangeService : IDisposable
{
    private readonly IEthernetFrameTransport _transport;
    private readonly MacAddress _source;
    private readonly MultiSlaveProcessImagePlan _plan;
    private readonly int _totalLength;
    private readonly ushort _expectedWorkingCounter;
    private readonly int _slaveCount;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _started;

    private readonly ushort[] _pendingControlwords;
    private AlState _alState = AlState.SafeOp;

    private byte _nextDatagramIndex;
    private long _sequenceCounter;

    // Mutated only on the cyclic thread; safe to read when building each cycle's snapshot (also on that thread).
    private readonly int[] _lastPositionActualValues;
    private readonly ushort[] _lastRawStatuswords;
    private readonly bool[] _lastFaultBits;
    private int _consecutiveFailures;
    private ushort _lastWkc;
    private bool _isDataFresh;
    private string? _lastError;

    /// <summary>Creates a service that exchanges one LRW datagram per cycle over <paramref name="transport"/>, covering the combined logical range every slave in <paramref name="plan"/> describes.</summary>
    /// <param name="transport">The transport to send LRW datagrams on and receive replies from.</param>
    /// <param name="source">Source MAC address to stamp on every outgoing frame.</param>
    /// <param name="plan">The combined process-image plan computed by <see cref="ProcessImageBuilder.BuildMulti"/> for the group of slaves to exchange with.</param>
    public MultiSlaveCyclicExchangeService(IEthernetFrameTransport transport, MacAddress source, MultiSlaveProcessImagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(plan);

        _transport = transport;
        _source = source;
        _plan = plan;
        _totalLength = plan.TotalLength;
        _expectedWorkingCounter = plan.ExpectedWorkingCounter;
        _slaveCount = plan.Slaves.Count;

        _pendingControlwords = new ushort[_slaveCount];
        for (var i = 0; i < _slaveCount; i++)
        {
            _pendingControlwords[i] = Ds402Controlword.DisableVoltage;
        }

        _lastPositionActualValues = new int[_slaveCount];
        _lastRawStatuswords = new ushort[_slaveCount];
        _lastFaultBits = new bool[_slaveCount];
    }

    /// <summary>Cycle period. Paced by a <see cref="Stopwatch"/> with drift correction, never a fixed <c>Thread.Sleep</c>. Defaults to 10 ms.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long one cycle's LRW exchange waits for a reply before that cycle counts as failed. Defaults to 5 ms.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Number of consecutive failed cycles (WKC mismatch or no reply) that stops the loop and raises <see cref="Faulted"/>. Defaults to 10.</summary>
    public int MaxConsecutiveFailures { get; set; } = 10;

    /// <summary><c>true</c> from <see cref="Start"/> until the background thread actually exits.</summary>
    public bool IsRunning { get; private set; }

    /// <summary><c>true</c> once <see cref="MaxConsecutiveFailures"/> consecutive failed cycles have stopped the loop.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>The AL state last reported via <see cref="SetAlState"/>; this service never reads any AL Status register itself.</summary>
    public AlState AlState => _alState;

    /// <summary>Raised at the end of every cycle (success or failure) with a fresh snapshot of every slave's observable state.</summary>
    public event Action<MultiSlaveProcessImageSnapshot>? StatusUpdated;

    /// <summary>Raised only on meaningful state changes — AL state change, WKC mismatch/missing reply, a Fault bit 0-&gt;1 transition on any slave, or the final fault/stop message — never once per tick.</summary>
    public event Action<string>? LogEmitted;

    /// <summary>Raised exactly once, from the cyclic thread, the moment <see cref="MaxConsecutiveFailures"/> consecutive failed cycles are observed and the loop stops itself.</summary>
    public event Action<string>? Faulted;

    /// <summary>Informs this service of the AL state to report going forward. Logs the transition via <see cref="LogEmitted"/> when it actually changes.</summary>
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
    /// Enqueues <paramref name="controlword"/> to be written into slave <paramref name="slaveIndex"/>'s
    /// outbound Controlword field on the next cycle (and every cycle after, until changed again for
    /// that slave). This is the sole way anything outside this class can influence any slave's DS402
    /// power state — it never drives any slave's Controlword on its own initiative, and every other
    /// slave's Controlword is left exactly as it was.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slaveIndex"/> is outside the group this service was constructed for.</exception>
    public void SetControlword(int slaveIndex, ushort controlword)
    {
        if (slaveIndex < 0 || slaveIndex >= _slaveCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slaveIndex), slaveIndex, $"This service was constructed for {_slaveCount} slave(s).");
        }

        Volatile.Write(ref _pendingControlwords[slaveIndex], controlword);
    }

    /// <summary>
    /// Starts the dedicated background thread (<see cref="ThreadPriority.Normal"/>, <c>IsBackground = true</c>).
    /// Intended to be wired as a <see cref="StateMachine.MultiSlaveAlStateMachine.TransitionToSafeOp"/>
    /// <c>onSafeOpRequested</c> callback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException($"{nameof(MultiSlaveCyclicExchangeService)} has already been started.");
        }

        IsRunning = true;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "EtherCAT.MultiSlaveCyclicExchange",
        };
        _thread.Start();
    }

    /// <summary>
    /// Requests the loop stop. The loop's own thread — never the caller's — then attempts one final
    /// LRW exchange with EVERY slave's Controlword forced to <see cref="Ds402Controlword.DisableVoltage"/>
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

                RunCycle(forceDisableVoltage: false);

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
                RunCycle(forceDisableVoltage: true);
            }
            catch (Exception ex)
            {
                LogEmitted?.Invoke($"Final safe-shutdown exchange threw: {ex.Message}");
            }

            IsRunning = false;
        }
    }

    /// <summary>
    /// Runs exactly one LRW exchange cycle: builds the outbound datagram for every slave (mirroring
    /// each slave's own PositionActualValue into its own TargetPosition and holding its own
    /// ModesOfOperation at 0, unconditionally), sends it as a single combined LRW, decodes the reply
    /// per slave (or records the failure), and raises <see cref="StatusUpdated"/>.
    /// </summary>
    private void RunCycle(bool forceDisableVoltage)
    {
        var outbound = new byte[_totalLength];

        for (var i = 0; i < _slaveCount; i++)
        {
            var slave = _plan.Slaves[i];
            var rxBuffer = new byte[slave.RxPdoLayout.TotalByteLength];
            var rx = new RxPdoImage(slave.RxPdoLayout, rxBuffer);

            // --- Safety invariant, per slave: unconditional, every cycle, no public API can change this. ---
            rx.ModesOfOperation = 0;
            rx.TargetPosition = _lastPositionActualValues[i];
            // -------------------------------------------------------------------------------------------------

            rx.Controlword = forceDisableVoltage ? Ds402Controlword.DisableVoltage : Volatile.Read(ref _pendingControlwords[i]);

            Array.Copy(rxBuffer, 0, outbound, slave.OutputsOffset, rxBuffer.Length);
        }

        var (wkc, replyData) = PerformLogicalExchange(outbound);
        var sequence = Interlocked.Increment(ref _sequenceCounter);

        var ok = replyData is not null && wkc == _expectedWorkingCounter;
        var slaveSnapshots = new List<SlaveProcessImageSnapshot>(_slaveCount);

        if (ok)
        {
            for (var i = 0; i < _slaveCount; i++)
            {
                var slave = _plan.Slaves[i];
                var txBuffer = new byte[slave.TxPdoLayout.TotalByteLength];
                Array.Copy(replyData!, _plan.TotalOutputsLength + slave.InputsOffset, txBuffer, 0, txBuffer.Length);
                var tx = new TxPdoImage(slave.TxPdoLayout, txBuffer);

                _lastPositionActualValues[i] = tx.PositionActualValue;
                _lastRawStatuswords[i] = tx.Statusword;

                var status = new Ds402Statusword(tx.Statusword);
                if (status.Fault && !_lastFaultBits[i])
                {
                    LogEmitted?.Invoke($"Slave {i} (station 0x{slave.StationAddress:X4}): Statusword Fault bit transitioned 0->1 (Statusword=0x{tx.Statusword:X4}).");
                }

                _lastFaultBits[i] = status.Fault;

                slaveSnapshots.Add(new SlaveProcessImageSnapshot(slave.StationAddress, tx.Statusword, status, tx.PositionActualValue));
            }

            _lastWkc = wkc;
            _isDataFresh = true;
            _lastError = null;
            _consecutiveFailures = 0;
        }
        else
        {
            _consecutiveFailures++;
            _lastWkc = wkc;
            _isDataFresh = false;
            _lastError = replyData is null
                ? $"No LRW reply observed within {ReplyTimeout.TotalMilliseconds:F0} ms."
                : $"LRW WKC mismatch: expected {_expectedWorkingCounter}, got {wkc}.";

            LogEmitted?.Invoke(_lastError);

            // Retained/stale, exactly like the single-slave CyclicExchangeService: every slave's
            // snapshot reflects the last cycle that did succeed, not this failed one.
            for (var i = 0; i < _slaveCount; i++)
            {
                var slave = _plan.Slaves[i];
                slaveSnapshots.Add(new SlaveProcessImageSnapshot(
                    slave.StationAddress,
                    _lastRawStatuswords[i],
                    new Ds402Statusword(_lastRawStatuswords[i]),
                    _lastPositionActualValues[i]));
            }
        }

        if (!ok && !IsFaulted && _consecutiveFailures >= MaxConsecutiveFailures)
        {
            IsFaulted = true;
            var message = $"{nameof(MultiSlaveCyclicExchangeService)} faulted: {_consecutiveFailures} consecutive failed cycles (last error: {_lastError}).";
            LogEmitted?.Invoke(message);
            Faulted?.Invoke(message);
        }

        var snapshot = new MultiSlaveProcessImageSnapshot(
            sequence,
            _alState,
            _lastWkc,
            _isDataFresh,
            _lastError,
            IsFaulted,
            slaveSnapshots);

        StatusUpdated?.Invoke(snapshot);
    }

    /// <summary>
    /// Sends one LRW datagram covering <paramref name="outboundData"/>.Length bytes starting at
    /// logical address 0 and waits for the matching reply. Built directly on <see cref="Protocol"/> +
    /// <see cref="IEthernetFrameTransport"/>, mirroring <see cref="CyclicExchangeService"/>'s own
    /// equivalent private helper.
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
