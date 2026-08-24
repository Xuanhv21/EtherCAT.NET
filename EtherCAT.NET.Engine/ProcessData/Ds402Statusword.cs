namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// Decoded view over a CiA 402 Statusword (0x6041) value: each bit exposed as a named boolean
/// rather than requiring callers to mask the raw <see cref="ushort"/> themselves. Construction is
/// the decode step — there is no separate "Decode" method to call.
/// </summary>
/// <remarks>
/// Bit layout (ETG.6010 / CiA 402):
/// bit0 Ready to switch on, bit1 Switched on, bit2 Operation enabled, bit3 Fault,
/// bit4 Voltage enabled, bit5 Quick stop, bit6 Switch on disabled, bit7 Warning.
/// For example <c>0x0040</c> decodes to only <see cref="SwitchOnDisabled"/> set, <c>0x0021</c> to
/// <see cref="ReadyToSwitchOn"/> + <see cref="QuickStop"/>, and <c>0x0037</c> to
/// <see cref="ReadyToSwitchOn"/> + <see cref="SwitchedOn"/> + <see cref="OperationEnabled"/> +
/// <see cref="VoltageEnabled"/> + <see cref="QuickStop"/>.
/// </remarks>
/// <param name="Raw">The raw, undecoded 16-bit Statusword value.</param>
public readonly record struct Ds402Statusword(ushort Raw)
{
    /// <summary>Bit 0 — Ready to switch on.</summary>
    public bool ReadyToSwitchOn => (Raw & 0x0001) != 0;

    /// <summary>Bit 1 — Switched on.</summary>
    public bool SwitchedOn => (Raw & 0x0002) != 0;

    /// <summary>Bit 2 — Operation enabled.</summary>
    public bool OperationEnabled => (Raw & 0x0004) != 0;

    /// <summary>Bit 3 — Fault.</summary>
    public bool Fault => (Raw & 0x0008) != 0;

    /// <summary>Bit 4 — Voltage enabled.</summary>
    public bool VoltageEnabled => (Raw & 0x0010) != 0;

    /// <summary>Bit 5 — Quick stop.</summary>
    public bool QuickStop => (Raw & 0x0020) != 0;

    /// <summary>Bit 6 — Switch on disabled.</summary>
    public bool SwitchOnDisabled => (Raw & 0x0040) != 0;

    /// <summary>Bit 7 — Warning.</summary>
    public bool Warning => (Raw & 0x0080) != 0;
}
