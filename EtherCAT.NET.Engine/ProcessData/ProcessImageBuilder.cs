using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// Computes a <see cref="ProcessImagePlan"/> from a matched <see cref="EsiDeviceDescriptor"/> and a
/// chosen RxPdo/TxPdo — the "don't hard-code offsets" layer described in the implementation plan's
/// "Byte offset PDO" section. Every byte offset, PDO length, and FMMU logical range is derived
/// purely by walking the ESI model; none of it is a magic constant.
/// </summary>
public static class ProcessImageBuilder
{
    /// <summary>PDO mapping index of the Milestone 1 fixed/default outputs (RxPDO) mapping.</summary>
    public const ushort DefaultRxPdoIndex = 0x1600;

    /// <summary>PDO mapping index of the Milestone 1 fixed/default inputs (TxPDO) mapping.</summary>
    public const ushort DefaultTxPdoIndex = 0x1A00;

    /// <summary>
    /// Builds the process-image plan for <paramref name="device"/> using its fixed/default
    /// Milestone 1 mappings, RxPDO <see cref="DefaultRxPdoIndex"/> (0x1600) for outputs and TxPDO
    /// <see cref="DefaultTxPdoIndex"/> (0x1A00) for inputs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="device"/> does not declare exactly one RxPdo/TxPdo with the expected index.
    /// </exception>
    public static ProcessImagePlan BuildDefault(EsiDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var rxPdo = FindPdo(device.RxPdos, DefaultRxPdoIndex, "RxPdo");
        var txPdo = FindPdo(device.TxPdos, DefaultTxPdoIndex, "TxPdo");

        return Build(device, rxPdo, txPdo);
    }

    /// <summary>
    /// Builds the process-image plan for <paramref name="device"/> using the explicitly chosen
    /// <paramref name="rxPdo"/> (outputs) and <paramref name="txPdo"/> (inputs) mappings.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Either PDO does not declare a <c>Sm</c> (SyncManager) attribute, or references a SyncManager
    /// index outside <paramref name="device"/>'s declared <see cref="EsiDeviceDescriptor.SyncManagers"/>.
    /// </exception>
    public static ProcessImagePlan Build(EsiDeviceDescriptor device, EsiPdo rxPdo, EsiPdo txPdo)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(rxPdo);
        ArgumentNullException.ThrowIfNull(txPdo);

        var rxLayout = BuildLayout(rxPdo);
        var txLayout = BuildLayout(txPdo);

        var outputsSyncManager = ResolveSyncManager(device, rxPdo);
        var inputsSyncManager = ResolveSyncManager(device, txPdo);

        var outputsFmmu = FmmuConfig.ForByteAlignedRegion(
            logicalStartAddress: 0,
            length: (ushort)rxLayout.TotalByteLength,
            physicalStartAddress: outputsSyncManager.StartAddress,
            readEnabled: false,
            writeEnabled: true);

        var inputsFmmu = FmmuConfig.ForByteAlignedRegion(
            logicalStartAddress: (uint)rxLayout.TotalByteLength,
            length: (ushort)txLayout.TotalByteLength,
            physicalStartAddress: inputsSyncManager.StartAddress,
            readEnabled: true,
            writeEnabled: false);

        return new ProcessImagePlan(rxLayout, txLayout, outputsFmmu, inputsFmmu);
    }

    /// <summary>
    /// Computes the byte layout of a single PDO mapping: walks <paramref name="pdo"/>'s entries in
    /// declared order, assigning each one the running byte-offset total and accumulating
    /// <c>BitLength / 8</c> for the next entry.
    /// </summary>
    public static PdoLayout BuildLayout(EsiPdo pdo)
    {
        ArgumentNullException.ThrowIfNull(pdo);

        var entries = new List<PdoEntryDescriptor>(pdo.Entries.Count);
        var byteOffset = 0;

        foreach (var entry in pdo.Entries)
        {
            entries.Add(new PdoEntryDescriptor(
                entry.Index,
                entry.SubIndex,
                (byte)entry.BitLength,
                entry.Name,
                byteOffset));

            byteOffset += entry.BitLength / 8;
        }

        return new PdoLayout(pdo.Index, pdo.Name, entries, byteOffset);
    }

    private static EsiPdo FindPdo(IReadOnlyList<EsiPdo> pdos, ushort index, string kind)
    {
        foreach (var pdo in pdos)
        {
            if (pdo.Index == index)
            {
                return pdo;
            }
        }

        throw new InvalidOperationException($"Device declares no {kind} with index 0x{index:X4}.");
    }

    private static EsiSyncManager ResolveSyncManager(EsiDeviceDescriptor device, EsiPdo pdo)
    {
        if (pdo.SyncManager is not { } smIndex)
        {
            throw new InvalidOperationException(
                $"PDO 0x{pdo.Index:X4} ('{pdo.Name}') does not declare a SyncManager (Sm attribute) in the ESI file.");
        }

        if (smIndex < 0 || smIndex >= device.SyncManagers.Count)
        {
            throw new InvalidOperationException(
                $"PDO 0x{pdo.Index:X4} references SyncManager index {smIndex}, but the device only declares {device.SyncManagers.Count} SyncManagers.");
        }

        return device.SyncManagers[smIndex];
    }
}
