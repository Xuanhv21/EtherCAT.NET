namespace EtherCAT.NET.Engine.Esi;

/// <summary>
/// One SyncManager (<c>&lt;Sm&gt;</c>) declared by a device, in document order. Values are taken
/// verbatim from the ESI file — nothing here is inferred.
/// </summary>
/// <param name="Index">Position of this SyncManager among the device's &lt;Sm&gt; elements (SM0, SM1, ...).</param>
/// <param name="Name">Usage name from the element text, e.g. "MBoxOut", "MBoxIn", "Outputs", "Inputs".</param>
/// <param name="StartAddress">ESC physical start address (the <c>StartAddress</c> attribute).</param>
/// <param name="ControlByte">SyncManager control register value (the <c>ControlByte</c> attribute).</param>
/// <param name="DefaultSize">SyncManager size in bytes (the <c>DefaultSize</c> attribute).</param>
/// <param name="Enable">Whether the SyncManager is enabled (the <c>Enable</c> attribute; defaults to <c>true</c> when absent).</param>
public sealed record EsiSyncManager(
    int Index,
    string Name,
    ushort StartAddress,
    byte ControlByte,
    ushort DefaultSize,
    bool Enable);

/// <summary>
/// One FMMU (<c>&lt;Fmmu&gt;</c>) declared by a device, in document order.
/// </summary>
/// <param name="Index">Position of this FMMU among the device's &lt;Fmmu&gt; elements (FMMU0, FMMU1, ...).</param>
/// <param name="Usage">Usage name from the element text, e.g. "Outputs", "Inputs", "MBoxState".</param>
public sealed record EsiFmmu(int Index, string Usage);

/// <summary>
/// One entry (<c>&lt;Entry&gt;</c>) inside a PDO mapping — a single CoE object that is mapped into
/// the cyclic process image.
/// </summary>
/// <param name="Index">CoE object index (the entry's &lt;Index&gt;).</param>
/// <param name="SubIndex">CoE object sub-index (the entry's &lt;SubIndex&gt;).</param>
/// <param name="BitLength">Size of the mapped value in bits (the entry's &lt;BitLen&gt;).</param>
/// <param name="Name">Descriptive name (the entry's &lt;Name&gt;), e.g. "Statusword".</param>
/// <param name="DataType">CoE data type name (the entry's &lt;DataType&gt;), e.g. "UINT", "DINT".</param>
public sealed record EsiPdoEntry(
    ushort Index,
    byte SubIndex,
    int BitLength,
    string Name,
    string DataType);

/// <summary>
/// One PDO mapping (<c>&lt;RxPdo&gt;</c> or <c>&lt;TxPdo&gt;</c>) declared by a device.
/// </summary>
/// <param name="Index">PDO mapping index, e.g. <c>0x1600</c> or <c>0x1A00</c>.</param>
/// <param name="Name">Descriptive name (the &lt;Name&gt; element), e.g. "Receive PDO mapping 1".</param>
/// <param name="SyncManager">SyncManager index this PDO is assigned to (the <c>Sm</c> attribute), when declared.</param>
/// <param name="Fixed">Whether the mapping is fixed and cannot be changed via SDO (the <c>Fixed</c> attribute).</param>
/// <param name="Entries">The mapped CoE objects, in document order.</param>
public sealed record EsiPdo(
    ushort Index,
    string Name,
    int? SyncManager,
    bool Fixed,
    IReadOnlyList<EsiPdoEntry> Entries);

/// <summary>
/// Mailbox configuration for a device: whether the DataLinkLayer mailbox protocol is used, its
/// request/response timeouts, and which CoE services the slave supports.
/// </summary>
public sealed record EsiMailboxConfig(
    bool DataLinkLayer,
    int RequestTimeout,
    int ResponseTimeout,
    bool CoeSdoInfo,
    bool CoePdoUpload,
    bool CoePdoAssign,
    bool CoePdoConfig,
    bool CoeSegmentedSdo,
    bool CoeCompleteAccess,
    bool CoeDiagHistory);

/// <summary>
/// One Distributed Clock operation mode (<c>&lt;Dc&gt;/&lt;OpMode&gt;</c>), e.g. "FreeRUN" or "DC SYNC0".
/// </summary>
/// <param name="Name">Short name (the &lt;Name&gt; element), e.g. "FreeRUN", "DC".</param>
/// <param name="Description">Description (the &lt;Desc&gt; element).</param>
/// <param name="AssignActivate">Value to write to the AssignActivate DC register (the &lt;AssignActivate&gt; element).</param>
/// <param name="CycleTimeSync0Factor">The <c>Factor</c> attribute on &lt;CycleTimeSync0&gt;.</param>
/// <param name="CycleTimeSync0">SYNC0 cycle time in nanoseconds (the &lt;CycleTimeSync0&gt; element).</param>
/// <param name="ShiftTimeSync0">SYNC0 shift time in nanoseconds (the &lt;ShiftTimeSync0&gt; element).</param>
public sealed record EsiOpMode(
    string Name,
    string Description,
    ulong AssignActivate,
    int CycleTimeSync0Factor,
    long CycleTimeSync0,
    long ShiftTimeSync0);

/// <summary>
/// Distributed Clock configuration for a device: the set of operation modes it supports.
/// </summary>
/// <param name="OpModes">The device's &lt;Dc&gt;/&lt;OpMode&gt; entries, in document order.</param>
public sealed record EsiDcConfig(IReadOnlyList<EsiOpMode> OpModes);

/// <summary>
/// One fully parsed &lt;Device&gt; block from an ESI file, decoupled from the raw XML shape.
/// </summary>
/// <param name="Name">Device name (the &lt;Type&gt; element's text), e.g. "MADLN01BE".</param>
/// <param name="ProductCode">Product code (the &lt;Type ProductCode="..."&gt; attribute) — matched against the value read from SII EEPROM.</param>
/// <param name="RevisionNumber">Revision number (the &lt;Type RevisionNo="..."&gt; attribute) — matched against the value read from SII EEPROM.</param>
/// <param name="SyncManagers">SyncManagers (&lt;Sm&gt;) in document order, typically SM0..SM3.</param>
/// <param name="Fmmus">FMMUs (&lt;Fmmu&gt;) in document order, typically FMMU0..FMMU2.</param>
/// <param name="RxPdos">Receive PDO mappings (&lt;RxPdo&gt;) — master-to-slave outputs.</param>
/// <param name="TxPdos">Transmit PDO mappings (&lt;TxPdo&gt;) — slave-to-master inputs.</param>
/// <param name="Mailbox">Mailbox/CoE configuration, or <c>null</c> when the device declares none.</param>
/// <param name="DistributedClock">Distributed Clock configuration, or <c>null</c> when the device declares none.</param>
public sealed record EsiDeviceDescriptor(
    string Name,
    uint ProductCode,
    uint RevisionNumber,
    IReadOnlyList<EsiSyncManager> SyncManagers,
    IReadOnlyList<EsiFmmu> Fmmus,
    IReadOnlyList<EsiPdo> RxPdos,
    IReadOnlyList<EsiPdo> TxPdos,
    EsiMailboxConfig? Mailbox,
    EsiDcConfig? DistributedClock);
