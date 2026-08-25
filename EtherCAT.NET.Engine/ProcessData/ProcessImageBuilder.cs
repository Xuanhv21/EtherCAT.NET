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
    /// Builds the combined process-image plan for a whole GROUP of slaves sharing one cyclic LRW
    /// exchange, using each slave's fixed/default Milestone 1 mappings (RxPDO
    /// <see cref="DefaultRxPdoIndex"/>, TxPDO <see cref="DefaultTxPdoIndex"/>), laid out
    /// outputs-then-inputs across the shared logical address space per
    /// <see cref="MultiSlaveProcessImagePlan"/>'s own layout convention (the single-slave
    /// <see cref="Build"/>'s FMMU0/FMMU1 convention, just repeated per slave).
    /// </summary>
    /// <param name="slaves">Every slave to include, in the order their outputs/inputs should be laid out (typically discovery order).</param>
    /// <exception cref="ArgumentException"><paramref name="slaves"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Some slave's device does not declare exactly one RxPdo/TxPdo with the expected index, or a PDO does not declare a valid SyncManager.</exception>
    public static MultiSlaveProcessImagePlan BuildMulti(IReadOnlyList<(ushort StationAddress, EsiDeviceDescriptor Device)> slaves)
    {
        ArgumentNullException.ThrowIfNull(slaves);
        if (slaves.Count == 0)
        {
            throw new ArgumentException("At least one slave is required.", nameof(slaves));
        }

        var rxPdos = new EsiPdo[slaves.Count];
        var txPdos = new EsiPdo[slaves.Count];
        var rxLayouts = new PdoLayout[slaves.Count];
        var txLayouts = new PdoLayout[slaves.Count];

        for (var i = 0; i < slaves.Count; i++)
        {
            rxPdos[i] = FindPdo(slaves[i].Device.RxPdos, DefaultRxPdoIndex, "RxPdo");
            txPdos[i] = FindPdo(slaves[i].Device.TxPdos, DefaultTxPdoIndex, "TxPdo");
            rxLayouts[i] = BuildLayout(rxPdos[i]);
            txLayouts[i] = BuildLayout(txPdos[i]);
        }

        var outputsOffsets = new int[slaves.Count];
        var outputsRunning = 0;
        for (var i = 0; i < slaves.Count; i++)
        {
            outputsOffsets[i] = outputsRunning;
            outputsRunning += rxLayouts[i].TotalByteLength;
        }

        var totalOutputs = outputsRunning;

        var inputsOffsets = new int[slaves.Count];
        var inputsRunning = 0;
        for (var i = 0; i < slaves.Count; i++)
        {
            inputsOffsets[i] = inputsRunning;
            inputsRunning += txLayouts[i].TotalByteLength;
        }

        var totalInputs = inputsRunning;

        var slaveImages = new List<SlaveProcessImage>(slaves.Count);
        for (var i = 0; i < slaves.Count; i++)
        {
            var outputsSyncManager = ResolveSyncManager(slaves[i].Device, rxPdos[i]);
            var inputsSyncManager = ResolveSyncManager(slaves[i].Device, txPdos[i]);

            var outputsFmmu = FmmuConfig.ForByteAlignedRegion(
                logicalStartAddress: (uint)outputsOffsets[i],
                length: (ushort)rxLayouts[i].TotalByteLength,
                physicalStartAddress: outputsSyncManager.StartAddress,
                readEnabled: false,
                writeEnabled: true);

            var inputsFmmu = FmmuConfig.ForByteAlignedRegion(
                logicalStartAddress: (uint)(totalOutputs + inputsOffsets[i]),
                length: (ushort)txLayouts[i].TotalByteLength,
                physicalStartAddress: inputsSyncManager.StartAddress,
                readEnabled: true,
                writeEnabled: false);

            slaveImages.Add(new SlaveProcessImage(
                slaves[i].StationAddress,
                slaves[i].Device,
                rxLayouts[i],
                txLayouts[i],
                outputsFmmu,
                inputsFmmu,
                outputsOffsets[i],
                inputsOffsets[i]));
        }

        return new MultiSlaveProcessImagePlan(slaveImages, totalOutputs, totalInputs);
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
