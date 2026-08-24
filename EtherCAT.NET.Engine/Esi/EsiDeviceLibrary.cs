namespace EtherCAT.NET.Engine.Esi;

/// <summary>
/// The EtherCAT vendor that publishes one or more device descriptions in an ESI file.
/// </summary>
/// <param name="Id">Vendor ID (e.g. <c>0x066F</c> for Panasonic).</param>
/// <param name="Name">Human-readable vendor name (English / LcId 1033 when available).</param>
public sealed record EsiVendor(uint Id, string Name);

/// <summary>
/// The fully parsed contents of an ESI (EtherCAT Slave Information) XML file: one vendor and the
/// flat list of every &lt;Device&gt; it describes. A single real-world ESI file commonly repeats
/// the same SyncManager/FMMU/PDO/Mailbox/DC structure across many &lt;Device&gt; blocks that only
/// differ by ProductCode/RevisionNo/Name (e.g. wattage variants of the same servo family) — all
/// of them are parsed into <see cref="Devices"/>, never just the first one.
/// </summary>
/// <param name="Vendor">The single vendor declared by the ESI file.</param>
/// <param name="Devices">Every device description found anywhere under &lt;Descriptions&gt;.</param>
public sealed record EsiDeviceLibrary(EsiVendor Vendor, IReadOnlyList<EsiDeviceDescriptor> Devices);
