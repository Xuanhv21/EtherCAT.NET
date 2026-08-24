namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// The 4-byte Address field of an EtherCAT datagram. The same 4 bytes carry either a node address
/// (ADP + ADO, low word first) or a single 32-bit logical address, depending on the datagram's
/// <see cref="EtherCatCommand"/> — see the remarks on that type. Both interpretations are exposed
/// here; which one is meaningful depends on the command the caller used to create the address.
/// </summary>
public readonly struct EtherCatAddress : IEquatable<EtherCatAddress>
{
    /// <summary>
    /// Auto-increment/configured Address Position, or the low 16 bits of a logical address.
    /// </summary>
    public ushort Adp { get; }

    /// <summary>
    /// Address Offset (typically an ESC register address), or the high 16 bits of a logical address.
    /// </summary>
    public ushort Ado { get; }

    private EtherCatAddress(ushort adp, ushort ado)
    {
        Adp = adp;
        Ado = ado;
    }

    /// <summary>The same 4 bytes read as a single little-endian 32-bit logical address.</summary>
    public uint LogicalAddress => (uint)Adp | ((uint)Ado << 16);

    /// <summary>
    /// Creates a node-addressed <see cref="EtherCatAddress"/> for APRD/APWR/APRW/FPRD/FPWR/FPRW/
    /// BRD/BWR/BRW/ARMW/FRMW datagrams.
    /// </summary>
    /// <param name="adp">Auto-increment position or Configured Station Address.</param>
    /// <param name="ado">ESC register offset.</param>
    public static EtherCatAddress ForNodeAddressed(ushort adp, ushort ado) => new(adp, ado);

    /// <summary>
    /// Creates a logically-addressed <see cref="EtherCatAddress"/> for LRD/LWR/LRW datagrams.
    /// </summary>
    public static EtherCatAddress ForLogicalAddressed(uint logicalAddress) =>
        new(unchecked((ushort)logicalAddress), unchecked((ushort)(logicalAddress >> 16)));

    /// <inheritdoc />
    public bool Equals(EtherCatAddress other) => Adp == other.Adp && Ado == other.Ado;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EtherCatAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Adp, Ado);

    public static bool operator ==(EtherCatAddress left, EtherCatAddress right) => left.Equals(right);

    public static bool operator !=(EtherCatAddress left, EtherCatAddress right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"Adp=0x{Adp:X4} Ado=0x{Ado:X4} (Logical=0x{LogicalAddress:X8})";
}
