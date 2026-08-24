namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// Named CiA 402 Controlword (0x6040) bit patterns. This is the single canonical table for
/// Milestone 1: <see cref="CyclicExchangeService"/> reuses <see cref="DisableVoltage"/> for its
/// Stop() safe-shutdown write, and the later WPF UI step's five DS402 buttons
/// (Shutdown/Switch On/Enable Operation/Disable Voltage/Fault Reset) are expected to reuse these
/// same constants rather than defining their own — per the implementation plan, there should only
/// ever be one Controlword-constants table in this codebase.
/// </summary>
public static class Ds402Controlword
{
    /// <summary>Disable Voltage — bit 1 (Enable Voltage) clear. The safe, do-nothing command word used to shut a drive down: <c>0x0000</c>.</summary>
    public const ushort DisableVoltage = 0x0000;

    /// <summary>Quick Stop — bits 1,2 set, bit 2 (Quick Stop) clear per the DS402 command table: <c>0x0002</c>.</summary>
    public const ushort QuickStop = 0x0002;

    /// <summary>Shutdown — transitions "Switch on disabled" -&gt; "Ready to switch on": <c>0x0006</c>.</summary>
    public const ushort Shutdown = 0x0006;

    /// <summary>Switch On — transitions "Ready to switch on" -&gt; "Switched on": <c>0x0007</c>.</summary>
    public const ushort SwitchOn = 0x0007;

    /// <summary>Enable Operation — transitions "Switched on" -&gt; "Operation enabled": <c>0x000F</c>.</summary>
    public const ushort EnableOperation = 0x000F;

    /// <summary>Fault Reset — bit 7, edge-triggered (0 -&gt; 1) to clear a Fault condition: <c>0x0080</c>.</summary>
    public const ushort FaultReset = 0x0080;
}
