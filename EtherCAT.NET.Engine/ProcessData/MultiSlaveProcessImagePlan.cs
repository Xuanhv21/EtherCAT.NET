using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// One slave's contribution to a <see cref="MultiSlaveProcessImagePlan"/>: its own computed RxPdo/
/// TxPdo byte layout, its own FMMU0/FMMU1 configuration (logical addresses placed within the shared,
/// combined logical address space the whole group's single LRW datagram covers — physical addresses
/// stay local to this slave's own ESC register space, exactly as on real hardware, since each slave's
/// registers are only ever reached via its own Configured Station Address), and this slave's byte
/// offset within the combined outputs/inputs regions.
/// </summary>
/// <param name="StationAddress">This slave's Configured Station Address, as assigned during discovery.</param>
/// <param name="Device">The matched ESI device descriptor this slave's layout/FMMUs were derived from.</param>
/// <param name="RxPdoLayout">Computed byte layout of this slave's outputs PDO.</param>
/// <param name="TxPdoLayout">Computed byte layout of this slave's inputs PDO.</param>
/// <param name="OutputsFmmu">This slave's FMMU0: logical range within the combined outputs region, mapped to this slave's own SM2 physical address, write-enabled only.</param>
/// <param name="InputsFmmu">This slave's FMMU1: logical range within the combined logical address space (starting after every slave's outputs), mapped to this slave's own SM3 physical address, read-enabled only.</param>
/// <param name="OutputsOffset">Byte offset of this slave's outputs within the combined outbound buffer (equals <see cref="OutputsFmmu"/>'s logical start address).</param>
/// <param name="InputsOffset">Byte offset of this slave's inputs within the combined INPUTS REGION (i.e. relative to the start of that region, not the whole buffer — add <see cref="MultiSlaveProcessImagePlan.TotalOutputsLength"/> to get the absolute offset).</param>
public sealed record SlaveProcessImage(
    ushort StationAddress,
    EsiDeviceDescriptor Device,
    PdoLayout RxPdoLayout,
    PdoLayout TxPdoLayout,
    FmmuConfig OutputsFmmu,
    FmmuConfig InputsFmmu,
    int OutputsOffset,
    int InputsOffset);

/// <summary>
/// The full process-image plan for a whole GROUP of slaves sharing one cyclic LRW exchange — the
/// multi-slave counterpart of <see cref="ProcessImagePlan"/>. Every slave's own
/// <see cref="SlaveProcessImage"/> is laid out outputs-first-then-inputs across the shared logical
/// address space, mirroring the single-slave plan's own FMMU0/FMMU1 convention just repeated per
/// slave: slave 0's outputs at logical <c>[0, len0)</c>, slave 1's outputs right after at
/// <c>[len0, len0+len1)</c>, and so on; then every slave's inputs the same way, starting at
/// <see cref="TotalOutputsLength"/>.
/// </summary>
/// <param name="Slaves">Every slave's own process image, in discovery order.</param>
/// <param name="TotalOutputsLength">Combined byte length of every slave's outputs region.</param>
/// <param name="TotalInputsLength">Combined byte length of every slave's inputs region.</param>
public sealed record MultiSlaveProcessImagePlan(
    IReadOnlyList<SlaveProcessImage> Slaves,
    int TotalOutputsLength,
    int TotalInputsLength)
{
    /// <summary>Total bytes the group's single LRW datagram carries: <see cref="TotalOutputsLength"/> + <see cref="TotalInputsLength"/>.</summary>
    public int TotalLength => TotalOutputsLength + TotalInputsLength;

    /// <summary>
    /// The Working Counter a fully healthy cycle must return: 2 per slave (one write-enabled FMMU +
    /// one read-enabled FMMU each, exactly like the single-slave <see cref="ProcessImagePlan"/>'s own
    /// expectation), summed across every slave in <see cref="Slaves"/>.
    /// </summary>
    public ushort ExpectedWorkingCounter => checked((ushort)(Slaves.Count * 2));
}
