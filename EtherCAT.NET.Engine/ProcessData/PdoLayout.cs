namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// The computed byte layout of one PDO mapping (RxPdo or TxPdo): every mapped entry's byte offset
/// within the PDO's data image, plus the image's total byte length — both derived purely by
/// walking the source <see cref="Esi.EsiPdo"/>'s entries in declared order, per the implementation
/// plan's "Byte offset PDO" section. Nothing here is hard-coded.
/// </summary>
/// <param name="PdoIndex">PDO mapping index, e.g. <c>0x1600</c> or <c>0x1A00</c>.</param>
/// <param name="Name">Descriptive name of the PDO (its <see cref="Esi.EsiPdo.Name"/>).</param>
/// <param name="Entries">Every mapped entry, in declared order, with its computed <see cref="PdoEntryDescriptor.ByteOffset"/>.</param>
/// <param name="TotalByteLength">Total byte length of this PDO's data image — the sum of every entry's <c>BitLength / 8</c>.</param>
public sealed record PdoLayout(
    ushort PdoIndex,
    string Name,
    IReadOnlyList<PdoEntryDescriptor> Entries,
    int TotalByteLength)
{
    /// <summary>
    /// Finds the entry mapping CoE object <paramref name="index"/>:<paramref name="subIndex"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No entry in this PDO maps that CoE object.</exception>
    public PdoEntryDescriptor GetEntry(ushort index, byte subIndex = 0)
    {
        foreach (var entry in Entries)
        {
            if (entry.Index == index && entry.SubIndex == subIndex)
            {
                return entry;
            }
        }

        throw new KeyNotFoundException(
            $"PDO 0x{PdoIndex:X4} has no entry mapping CoE object 0x{index:X4}:{subIndex:X2}.");
    }
}
