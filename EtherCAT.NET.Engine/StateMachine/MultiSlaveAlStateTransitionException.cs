using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.StateMachine;

/// <summary>
/// The multi-slave counterpart of <see cref="AlStateTransitionException"/>: thrown by
/// <see cref="MultiSlaveAlStateMachine"/> when some slave in the group did not reach a requested AL
/// state, identifying exactly which one — by both its index within the group and its Configured
/// Station Address — alongside the same attempted/actual state and AL Status Code detail the
/// single-slave exception carries.
/// </summary>
public sealed class MultiSlaveAlStateTransitionException : Exception
{
    /// <summary>Index (within the group the failing <see cref="MultiSlaveAlStateMachine"/> was constructed for) of the slave that failed to transition.</summary>
    public int SlaveIndex { get; }

    /// <summary>Configured Station Address of the slave that failed to transition.</summary>
    public ushort StationAddress { get; }

    /// <summary>The AL state the group was trying to reach when this slave was found not to have reached it.</summary>
    public AlState AttemptedState { get; }

    /// <summary>The AL state actually reported by this slave at the moment of failure.</summary>
    public AlState ActualState { get; }

    /// <summary>The AL Status Code (0x0134) read from this slave at the moment of failure.</summary>
    public AlStatusCode StatusCode { get; }

    /// <summary><c>true</c> when this was raised because the shared transition timeout elapsed; <c>false</c> when this slave actively refused the transition before the timeout.</summary>
    public bool TimedOut { get; }

    /// <summary>Creates a <see cref="MultiSlaveAlStateTransitionException"/> identifying which slave in the group failed to transition, and why.</summary>
    public MultiSlaveAlStateTransitionException(int slaveIndex, ushort stationAddress, AlState attemptedState, AlState actualState, AlStatusCode statusCode, bool timedOut)
        : base(BuildMessage(slaveIndex, stationAddress, attemptedState, actualState, statusCode, timedOut))
    {
        SlaveIndex = slaveIndex;
        StationAddress = stationAddress;
        AttemptedState = attemptedState;
        ActualState = actualState;
        StatusCode = statusCode;
        TimedOut = timedOut;
    }

    private static string BuildMessage(int slaveIndex, ushort stationAddress, AlState attemptedState, AlState actualState, AlStatusCode statusCode, bool timedOut) =>
        timedOut
            ? $"Slave {slaveIndex} (station 0x{stationAddress:X4}): timed out waiting for AL state {attemptedState}; it remained in {actualState}. AL Status Code (0x0134): {statusCode}."
            : $"Slave {slaveIndex} (station 0x{stationAddress:X4}): refused the transition to AL state {attemptedState} and remained in {actualState}. AL Status Code (0x0134): {statusCode}.";
}
