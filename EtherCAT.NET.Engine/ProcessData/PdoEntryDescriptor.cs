namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// One CoE object mapped into a PDO's data image, together with the byte offset it occupies
/// within that image — computed by <see cref="ProcessImageBuilder"/> from an
/// <see cref="Esi.EsiPdo"/>'s entries, never hard-coded.
/// </summary>
/// <param name="Index">CoE object index (matches <see cref="Esi.EsiPdoEntry.Index"/>).</param>
/// <param name="SubIndex">CoE object sub-index (matches <see cref="Esi.EsiPdoEntry.SubIndex"/>).</param>
/// <param name="BitLength">Size of the mapped value in bits.</param>
/// <param name="Name">Descriptive name (e.g. "Statusword"), or <c>null</c> when the ESI entry left it blank.</param>
/// <param name="ByteOffset">Byte offset of this entry within its PDO's data image — the sum of every preceding entry's <c>BitLength / 8</c>.</param>
public sealed record PdoEntryDescriptor(
    ushort Index,
    byte SubIndex,
    byte BitLength,
    string? Name,
    int ByteOffset);
