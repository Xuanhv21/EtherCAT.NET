namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// The EtherCAT Application Layer (AL) state, encoded in the low 4 bits of both the AL Control
/// register (0x0120, what the master requests) and the AL Status register (0x0130, what the slave
/// actually reports). Milestone 1 only ever requests <see cref="Init"/>, <see cref="PreOp"/>,
/// <see cref="SafeOp"/> and <see cref="Op"/>, in that order; <see cref="Bootstrap"/> is included for
/// completeness (it is a sibling of PreOp reachable only from Init, used for firmware update, out of
/// scope here).
/// </summary>
public enum AlState : ushort
{
    /// <summary>Initial state after power-up/reset. No mailbox, no process data.</summary>
    Init = 1,

    /// <summary>Mailbox communication (SM0/SM1) is available; no process data yet.</summary>
    PreOp = 2,

    /// <summary>Firmware update state, reachable only from Init. Not used in Milestone 1.</summary>
    Bootstrap = 3,

    /// <summary>Process data Sync Managers (SM2/SM3) are configured and inputs are valid; outputs are not yet applied by the slave.</summary>
    SafeOp = 4,

    /// <summary>Full operational exchange: both inputs and outputs are active.</summary>
    Op = 8,
}
