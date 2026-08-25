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
/// <param name="IsJogging"><c>true</c> whenever this slave's internal jog velocity is currently non-zero (see <see cref="MultiSlaveCyclicExchangeService.SetJog"/>) — including the deceleration tail after a jog button is released, not just while a direction is actively held.</param>
public sealed record SlaveProcessImageSnapshot(
    ushort StationAddress,
    ushort RawStatusword,
    Ds402Statusword Status,
    int PositionActualValue,
    bool IsJogging);

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
/// <b>Safety invariant (structural), per slave — TargetPosition always derives from actual, never
/// an independent trajectory:</b> every single cycle, for every slave in the group independently,
/// this class computes <c>TargetPosition = PositionActualValue</c> (read back on the immediately
/// preceding successful cycle) <c>+ jogPositionIncrement</c>, before the outbound datagram is ever
/// sent. <c>jogPositionIncrement</c> is derived, entirely inside this class, from an internal
/// per-slave jog velocity that <see cref="SetJog"/> can only ever nudge toward
/// <c>direction * </c><see cref="JogVelocity"/> at a rate bounded by <see cref="JogAcceleration"/>
/// (speeding up) or <see cref="JogDeceleration"/> (slowing down) — never set directly, and forced
/// back to exactly 0 the instant the slave's own Statusword stops confirming CiA 402
/// "Operation enabled" (see <see cref="ResolveJogPositionIncrement"/>). With that internal velocity
/// at 0 — the default, and the case whenever jog is not held/renewed, has fully decelerated, or the
/// slave is not enabled — <c>jogPositionIncrement</c> is 0 and this degenerates to exactly the
/// original Milestone 1 invariant, "always mirror actual, never an independently accumulated
/// target". Because the increment is (re-)derived from actual every cycle rather than compounded
/// onto a remembered target, the worst-case gap between commanded and actual position is bounded by
/// roughly one cycle's worth of motion at the current jog velocity, not unbounded. The only
/// externally reachable control surfaces for driving a slave are <see cref="SetControlword"/> (DS402
/// power state) and <see cref="SetJog"/> (bounded, heartbeat-gated, ramped position jog) — both
/// always target exactly one slave by index, never the whole group.
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

    // Jog request state: written by SetJog (any thread), read/cleared only on the cyclic thread.
    // -1/0/+1 per slave; _jogHeartbeatDeadlineTicks is an Environment.TickCount64 deadline, renewed
    // by every SetJog call with a non-zero direction. _cspModeEngaged latches true the first time a
    // slave's jog is actually applied and never reverts to false -- see the type-level remarks.
    private readonly int[] _jogDirection;
    private readonly long[] _jogHeartbeatDeadlineTicks;
    private readonly bool[] _cspModeEngaged;

    // Jog ramp state: touched only on the cyclic thread, inside ResolveJogPositionIncrement.
    // _currentJogVelocity is the actual, ramped velocity (raw units/sec, signed) this cycle is
    // driving toward direction*JogVelocity; _jogPositionRemainder is the fractional (< 1 count)
    // leftover from converting that velocity into whole-count position increments each cycle, so
    // slow jog speeds are not silently truncated to "never actually moves" -- see
    // ResolveJogPositionIncrement's own remarks.
    private readonly double[] _currentJogVelocity;
    private readonly double[] _jogPositionRemainder;

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

        _jogDirection = new int[_slaveCount];
        _jogHeartbeatDeadlineTicks = new long[_slaveCount];
        _cspModeEngaged = new bool[_slaveCount];

        _currentJogVelocity = new double[_slaveCount];
        _jogPositionRemainder = new double[_slaveCount];
    }

    /// <summary>Cycle period. Paced by a <see cref="Stopwatch"/> with drift correction, never a fixed <c>Thread.Sleep</c>. Defaults to 10 ms.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long one cycle's LRW exchange waits for a reply before that cycle counts as failed. Defaults to 5 ms.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Number of consecutive failed cycles (WKC mismatch or no reply) that stops the loop and raises <see cref="Faulted"/>. Defaults to 10.</summary>
    public int MaxConsecutiveFailures { get; set; } = 10;

    /// <summary>
    /// The jog speed a slave ramps toward while <see cref="SetJog"/> holds a non-zero direction for
    /// it, in raw position units per second. There is no unit scaling anywhere in this milestone, so
    /// this is raw encoder counts/sec, not mm/degrees/RPM — start small and increase gradually while
    /// watching the real motor. Defaults to a deliberately conservative 50 units/sec.
    /// </summary>
    public double JogVelocity { get; set; } = 50.0;

    /// <summary>
    /// How quickly a slave's internal jog velocity is allowed to ramp UP toward
    /// <see cref="JogVelocity"/> (raw units/sec², i.e. how many units/sec of speed it gains per
    /// second) — never applied instantaneously. Defaults to 200 units/sec² (0 to <see cref="JogVelocity"/>'s
    /// default in a quarter second).
    /// </summary>
    public double JogAcceleration { get; set; } = 200.0;

    /// <summary>
    /// How quickly a slave's internal jog velocity is allowed to ramp DOWN toward 0 (raw units/sec²)
    /// — applied on release, on a jog-heartbeat timeout, and whenever the requested speed decreases
    /// (including a direction reversal while still moving). Deliberately defaults higher than
    /// <see cref="JogAcceleration"/> (400 units/sec²) — stopping promptly is a more conservative
    /// default than starting promptly.
    /// </summary>
    public double JogDeceleration { get; set; } = 400.0;

    /// <summary>
    /// How long a non-zero jog direction set via <see cref="SetJog"/> stays active without being
    /// renewed before the cyclic thread itself safely treats it as released (and begins decelerating
    /// via <see cref="JogDeceleration"/>) — independent of whether the caller ever explicitly calls
    /// <see cref="SetJog"/> with 0. The caller (the UI, for as long as a jog button is actually held)
    /// must call <see cref="SetJog"/> again at a shorter interval than this to keep jogging; if it
    /// stops (a missed mouse-up event, a UI hang, a crashed dispatcher timer), jogging auto-releases
    /// within this timeout rather than continuing indefinitely. Defaults to 250 ms.
    /// </summary>
    public TimeSpan JogHeartbeatTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

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
    /// Requests slave <paramref name="slaveIndex"/> jog in <paramref name="direction"/>: its internal
    /// jog velocity ramps toward <c>direction * </c><see cref="JogVelocity"/> at up to
    /// <see cref="JogAcceleration"/>/<see cref="JogDeceleration"/> per second — never jumping there
    /// instantly. Must be called again within <see cref="JogHeartbeatTimeout"/> to keep jogging — see
    /// that property's remarks. The requested direction only ever actually moves anything once this
    /// slave's own Statusword confirms CiA 402 "Operation enabled" (see
    /// <see cref="ResolveJogPositionIncrement"/>): holding a jog button before that, or after voltage
    /// is disabled, has no effect and resets the internal velocity to 0, so an Enable happening while
    /// a jog button is already held always starts a fresh ramp-up rather than inheriting whatever
    /// velocity was stored from before. The first time a slave's jog velocity actually becomes
    /// non-zero, Modes of operation permanently switches from 0 to 8 (CSP) for that slave and never
    /// reverts — safe to do because TargetPosition always stays within about one cycle's motion of
    /// actual regardless, per the type-level remarks.
    /// </summary>
    /// <param name="slaveIndex">Which slave to jog.</param>
    /// <param name="direction">-1 (jog toward decreasing position), 0 (release — decelerate to stop), or +1 (jog toward increasing position).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slaveIndex"/> is outside the group this service was constructed for, or <paramref name="direction"/> is not -1, 0, or +1.</exception>
    public void SetJog(int slaveIndex, int direction)
    {
        if (slaveIndex < 0 || slaveIndex >= _slaveCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slaveIndex), slaveIndex, $"This service was constructed for {_slaveCount} slave(s).");
        }

        if (direction < -1 || direction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be -1, 0, or +1.");
        }

        Volatile.Write(ref _jogDirection[slaveIndex], direction);
        if (direction != 0)
        {
            Volatile.Write(ref _jogHeartbeatDeadlineTicks[slaveIndex], Environment.TickCount64 + (long)JogHeartbeatTimeout.TotalMilliseconds);
        }
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
    /// each slave's own PositionActualValue, plus its current bounded jog increment if any, into its
    /// own TargetPosition, and holding its own ModesOfOperation at 0 until jog first engages it),
    /// sends it as a single combined LRW, decodes the reply per slave (or records the failure), and
    /// raises <see cref="StatusUpdated"/>.
    /// </summary>
    private void RunCycle(bool forceDisableVoltage)
    {
        var outbound = new byte[_totalLength];
        var isJogging = new bool[_slaveCount];

        for (var i = 0; i < _slaveCount; i++)
        {
            var slave = _plan.Slaves[i];
            var rxBuffer = new byte[slave.RxPdoLayout.TotalByteLength];
            var rx = new RxPdoImage(slave.RxPdoLayout, rxBuffer);

            var increment = forceDisableVoltage ? 0 : ResolveJogPositionIncrement(i);
            isJogging[i] = !forceDisableVoltage && _currentJogVelocity[i] != 0.0;

            // --- Safety invariant, per slave: unconditional, every cycle, no public API can bypass
            // this -- TargetPosition is always actual plus at most one cycle's worth of bounded jog
            // motion, never an independently accumulated trajectory. See the type-level remarks. ---
            rx.ModesOfOperation = Volatile.Read(ref _cspModeEngaged[i]) ? (sbyte)8 : (sbyte)0;
            rx.TargetPosition = _lastPositionActualValues[i] + increment;
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

                slaveSnapshots.Add(new SlaveProcessImageSnapshot(slave.StationAddress, tx.Statusword, status, tx.PositionActualValue, isJogging[i]));
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
                    _lastPositionActualValues[i],
                    isJogging[i]));
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
    /// Resolves slave <paramref name="slaveIndex"/>'s whole-count TargetPosition offset for THIS
    /// cycle. Only ever called with <c>forceDisableVoltage == false</c> — see <see cref="RunCycle"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order of operations: (1) if the requested direction's heartbeat has lapsed, treat it as
    /// released (logged once) — same as an explicit <see cref="SetJog"/> 0; (2) if the slave's own
    /// most recently observed Statusword does not show CiA 402 "Operation enabled" (silently — this
    /// is the expected, unremarkable state while a user is holding a jog button before/after
    /// enabling, not a fault), hard-reset the internal velocity and fractional remainder to exactly 0
    /// and return 0 — the drive ignores TargetPosition either way while not enabled, but this
    /// guarantees a later Enable always starts a fresh, safe ramp-up rather than resuming a stale
    /// velocity; (3) otherwise ramp the internal velocity toward
    /// <c>direction * </c><see cref="JogVelocity"/> by at most
    /// <see cref="JogAcceleration"/>/<see cref="JogDeceleration"/> (whichever applies — acceleration
    /// when the magnitude is increasing, deceleration when it is decreasing, including a direction
    /// reversal while still moving, which this simple model ramps through at the acceleration rate
    /// rather than decelerating-then-accelerating in two distinct phases: a deliberate simplification
    /// for a "basic" jog feature, not an exact trapezoidal-profile reversal).
    /// </para>
    /// <para>
    /// Converting that velocity into a whole-count position increment every 10 ms cycle would
    /// silently truncate any jog speed below ~100 raw units/sec to "never actually moves" (0.5 units
    /// x 0.01 s rounds to 0 every single cycle). To avoid that without ever letting TargetPosition
    /// drift independently of actual by more than about one cycle's motion, the fractional part of
    /// <c>velocity * cycleSeconds</c> that does not fit in this cycle's integer increment is carried
    /// over as <c>_jogPositionRemainder</c> and added into next cycle's computation — a running
    /// remainder, not a running position — so a slow jog speed still averages out to the exact right
    /// number of counts per second over time, while any single cycle's offset from actual stays
    /// bounded to (this cycle's ideal increment, rounded down, plus at most one count of carry).
    /// </para>
    /// </remarks>
    private int ResolveJogPositionIncrement(int slaveIndex)
    {
        var direction = Volatile.Read(ref _jogDirection[slaveIndex]);

        if (direction != 0)
        {
            var deadline = Volatile.Read(ref _jogHeartbeatDeadlineTicks[slaveIndex]);
            if (Environment.TickCount64 > deadline)
            {
                Volatile.Write(ref _jogDirection[slaveIndex], 0);
                LogEmitted?.Invoke($"Slave {slaveIndex} (station 0x{_plan.Slaves[slaveIndex].StationAddress:X4}): jog heartbeat timed out; stopping.");
                direction = 0;
            }
        }

        if (!new Ds402Statusword(_lastRawStatuswords[slaveIndex]).OperationEnabled)
        {
            _currentJogVelocity[slaveIndex] = 0.0;
            _jogPositionRemainder[slaveIndex] = 0.0;
            return 0;
        }

        if (direction != 0)
        {
            Volatile.Write(ref _cspModeEngaged[slaveIndex], true);
        }

        var cycleSeconds = Period.TotalSeconds;
        var requestedVelocity = direction * JogVelocity;
        var current = _currentJogVelocity[slaveIndex];

        var rampRate = Math.Abs(requestedVelocity) > Math.Abs(current) ? JogAcceleration : JogDeceleration;
        var maxDelta = Math.Abs(rampRate) * cycleSeconds;

        if (current < requestedVelocity)
        {
            current = Math.Min(current + maxDelta, requestedVelocity);
        }
        else if (current > requestedVelocity)
        {
            current = Math.Max(current - maxDelta, requestedVelocity);
        }

        _currentJogVelocity[slaveIndex] = current;

        var rawIncrement = (current * cycleSeconds) + _jogPositionRemainder[slaveIndex];
        var wholeIncrement = (int)Math.Truncate(rawIncrement);
        _jogPositionRemainder[slaveIndex] = rawIncrement - wholeIncrement;

        return wholeIncrement;
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
