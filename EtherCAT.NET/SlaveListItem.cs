namespace EtherCAT.NET;

/// <summary>
/// One discovered slave, as shown in the slave picker: its position within the group (matching its
/// index into <see cref="Engine.ProcessData.MultiSlaveProcessImageSnapshot.Slaves"/> and every other
/// per-slave list the multi-slave engine types expose), its Configured Station Address, and the
/// matched ESI device's name. <see cref="MainWindowViewModel.SelectedSlave"/> determines which
/// slave's Statusword panel and Controlword buttons are currently live.
/// </summary>
/// <param name="Index">Position of this slave within the discovered group (0-based).</param>
/// <param name="StationAddress">This slave's Configured Station Address, as assigned during discovery.</param>
/// <param name="DeviceName">The matched ESI device's name (e.g. "MADLN01BE").</param>
public sealed record SlaveListItem(int Index, ushort StationAddress, string DeviceName)
{
    /// <inheritdoc />
    public override string ToString() => $"[{Index}] 0x{StationAddress:X4} — {DeviceName}";
}
