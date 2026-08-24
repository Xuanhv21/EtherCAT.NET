using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// The full Milestone 1 process-image plan for one matched slave: the computed byte layout of its
/// chosen RxPdo (outputs) and TxPdo (inputs), plus the FMMU0/FMMU1 logical-address plan derived
/// from those layouts and the PDOs' assigned SyncManagers, per the implementation plan's "Byte
/// offset PDO" section.
/// </summary>
/// <param name="RxPdoLayout">Computed byte layout of the outputs PDO (e.g. 0x1600).</param>
/// <param name="TxPdoLayout">Computed byte layout of the inputs PDO (e.g. 0x1A00).</param>
/// <param name="OutputsFmmu">
/// FMMU0 configuration: logical range <c>[0, RxPdoLayout.TotalByteLength)</c>, mapped to the
/// physical address of the RxPdo's SyncManager, write-enabled only.
/// </param>
/// <param name="InputsFmmu">
/// FMMU1 configuration: logical range starting immediately after <see cref="OutputsFmmu"/>'s range
/// and running for <c>TxPdoLayout.TotalByteLength</c> bytes, mapped to the physical address of the
/// TxPdo's SyncManager, read-enabled only.
/// </param>
public sealed record ProcessImagePlan(
    PdoLayout RxPdoLayout,
    PdoLayout TxPdoLayout,
    FmmuConfig OutputsFmmu,
    FmmuConfig InputsFmmu);
