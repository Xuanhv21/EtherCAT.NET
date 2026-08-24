namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// EtherCAT datagram command codes (ETG.1000.4 Table 5). The command determines how an
/// <see cref="EtherCatAddress"/> is interpreted: every command except <see cref="Lrd"/>,
/// <see cref="Lwr"/> and <see cref="Lrw"/> is node-addressed (ADP = slave position or configured
/// station address, ADO = ESC register offset); the logical commands instead treat the same 4
/// address bytes as a single 32-bit logical address used by FMMUs.
/// </summary>
public enum EtherCatCommand : byte
{
    /// <summary>No operation — the datagram is ignored by every slave.</summary>
    Nop = 0,

    /// <summary>Auto-increment physical read. ADP is a negative position offset, decremented by each slave.</summary>
    Aprd = 1,

    /// <summary>Auto-increment physical write.</summary>
    Apwr = 2,

    /// <summary>Auto-increment physical read/write.</summary>
    Aprw = 3,

    /// <summary>Configured-address physical read. ADP is the slave's Configured Station Address.</summary>
    Fprd = 4,

    /// <summary>Configured-address physical write.</summary>
    Fpwr = 5,

    /// <summary>Configured-address physical read/write.</summary>
    Fprw = 6,

    /// <summary>Broadcast read. Every slave ORs its data into the datagram and increments the WKC.</summary>
    Brd = 7,

    /// <summary>Broadcast write. Every slave ANDs the datagram data into its memory.</summary>
    Bwr = 8,

    /// <summary>Broadcast read/write.</summary>
    Brw = 9,

    /// <summary>Logical memory read, mapped through slave FMMUs.</summary>
    Lrd = 10,

    /// <summary>Logical memory write, mapped through slave FMMUs.</summary>
    Lwr = 11,

    /// <summary>Logical memory read/write (used for the cyclic process data exchange), mapped through slave FMMUs.</summary>
    Lrw = 12,

    /// <summary>Auto-increment physical read, multiple write.</summary>
    Armw = 13,

    /// <summary>Configured-address physical read, multiple write.</summary>
    Frmw = 14,
}
