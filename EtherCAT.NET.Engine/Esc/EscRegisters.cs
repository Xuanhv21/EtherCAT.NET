namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// ESC (EtherCAT Slave Controller) register addresses used by <see cref="EscClient"/>, as tabulated
/// in the implementation plan's "ESC register map" section. Every address is a byte offset into the
/// slave's register address space, accessed via FPRD/FPWR (node-addressed) once a Configured Station
/// Address has been assigned, or via APWR (auto-increment addressed) for
/// <see cref="ConfiguredStationAddressRegister"/> during discovery.
/// </summary>
public static class EscRegisters
{
    /// <summary>
    /// Configured Station Address register. Written via APWR (ADP = auto-increment position, ADO =
    /// this register) during discovery to assign each slave a fixed address usable with FPRD/FPWR.
    /// </summary>
    public const ushort ConfiguredStationAddressRegister = 0x0010;

    /// <summary>AL Control register — the master writes the requested AL state here.</summary>
    public const ushort AlControlRegister = 0x0120;

    /// <summary>AL Status register — low 4 bits are the current <see cref="AlState"/>, bit 4 (0x0010) is the Error flag.</summary>
    public const ushort AlStatusRegister = 0x0130;

    /// <summary>AL Status Code register — set by the slave alongside the AL Status Error flag to explain a refused state transition.</summary>
    public const ushort AlStatusCodeRegister = 0x0134;

    /// <summary>SII EEPROM Control/Status register (2 bytes). Bit 8 requests a read; bit 15 is Busy.</summary>
    public const ushort SiiControlRegister = 0x0502;

    /// <summary>SII EEPROM Address register (4 bytes) — the word address to read/write, written before triggering the operation via <see cref="SiiControlRegister"/>.</summary>
    public const ushort SiiAddressRegister = 0x0504;

    /// <summary>SII EEPROM Data register (8 bytes, only the first 4 used for the word reads this client performs) — holds the result once <see cref="SiiControlRegister"/>'s Busy bit clears.</summary>
    public const ushort SiiDataRegister = 0x0508;

    /// <summary>Base register address of FMMU0's 16-byte configuration block; FMMU<c>n</c> is at <see cref="FmmuBaseRegister"/> + n * <see cref="FmmuStride"/>.</summary>
    public const ushort FmmuBaseRegister = 0x0600;

    /// <summary>Byte distance between consecutive FMMU configuration blocks.</summary>
    public const int FmmuStride = 0x10;

    /// <summary>Number of FMMU configuration blocks the plan uses (Outputs, Inputs, MBoxState).</summary>
    public const int FmmuCount = 3;

    /// <summary>Base register address of SM0's 8-byte configuration block; SM<c>n</c> is at <see cref="SmBaseRegister"/> + n * <see cref="SmStride"/>.</summary>
    public const ushort SmBaseRegister = 0x0800;

    /// <summary>Byte distance between consecutive Sync Manager configuration blocks.</summary>
    public const int SmStride = 0x08;

    /// <summary>Number of Sync Manager configuration blocks the plan uses (MBoxOut, MBoxIn, Outputs, Inputs).</summary>
    public const int SmCount = 4;

    /// <summary>Register address of FMMU block <paramref name="index"/> (0-based).</summary>
    public static ushort FmmuAddress(int index) =>
        (ushort)(FmmuBaseRegister + (index * FmmuStride));

    /// <summary>Register address of Sync Manager block <paramref name="index"/> (0-based).</summary>
    public static ushort SmAddress(int index) =>
        (ushort)(SmBaseRegister + (index * SmStride));
}
