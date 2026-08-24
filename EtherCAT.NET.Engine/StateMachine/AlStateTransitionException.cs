using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.StateMachine;

/// <summary>
/// Thrown by <see cref="AlStateMachine"/> whenever a requested AL state transition does not
/// succeed — either the slave explicitly refused it (AL Status Error flag set) or polling gave up
/// after <see cref="AlStateMachine.TransitionTimeout"/> without the slave ever reporting the
/// requested state. Per the implementation plan's error-handling rule, a failed/timed-out
/// transition must never surface as a bare timeout: this exception always carries the AL state
/// that was attempted, the AL state the slave actually stayed at, and the AL Status Code (0x0134)
/// read from the slave at the moment the caller gave up — including its human-readable
/// <see cref="Esc.AlStatusCode.Description"/> — so the real cause is visible rather than just "no
/// response in time".
/// </summary>
public sealed class AlStateTransitionException : Exception
{
    /// <summary>The AL state <see cref="AlStateMachine"/> was trying to reach when it gave up.</summary>
    public AlState AttemptedState { get; }

    /// <summary>The AL state actually reported by the slave (AL Status, 0x0130) at the moment of failure.</summary>
    public AlState ActualState { get; }

    /// <summary>
    /// The AL Status Code (0x0134) read from the slave at the moment of failure. When
    /// <see cref="TimedOut"/> is <c>true</c> and the slave never raised the AL Status Error flag,
    /// this is typically <see cref="Esc.AlStatusCode.NoError"/> — the slave simply never reached the
    /// requested state in time, rather than actively refusing it; callers should inspect
    /// <see cref="TimedOut"/> to tell the two cases apart.
    /// </summary>
    public AlStatusCode StatusCode { get; }

    /// <summary>
    /// <c>true</c> when this exception was raised because <see cref="AlStateMachine.TransitionTimeout"/>
    /// elapsed without the slave ever reaching <see cref="AttemptedState"/>; <c>false</c> when the
    /// slave actively refused the transition (AL Status Error flag set) before the timeout.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>Creates an <see cref="AlStateTransitionException"/> describing a failed AL state transition.</summary>
    public AlStateTransitionException(AlState attemptedState, AlState actualState, AlStatusCode statusCode, bool timedOut)
        : base(BuildMessage(attemptedState, actualState, statusCode, timedOut))
    {
        AttemptedState = attemptedState;
        ActualState = actualState;
        StatusCode = statusCode;
        TimedOut = timedOut;
    }

    private static string BuildMessage(AlState attemptedState, AlState actualState, AlStatusCode statusCode, bool timedOut) =>
        timedOut
            ? $"Timed out waiting for AL state {attemptedState}; the slave remained in {actualState}. AL Status Code (0x0134): {statusCode}."
            : $"The slave refused the transition to AL state {attemptedState} and remained in {actualState}. AL Status Code (0x0134): {statusCode}.";
}
