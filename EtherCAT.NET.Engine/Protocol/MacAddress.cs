namespace EtherCAT.NET.Engine.Protocol;

/// <summary>
/// A 6-byte Ethernet MAC address, as used for the destination/source fields of an
/// <see cref="EthernetFrame"/>.
/// </summary>
public readonly struct MacAddress : IEquatable<MacAddress>
{
    /// <summary>Length in bytes of a MAC address.</summary>
    public const int Length = 6;

    private readonly byte _b0;
    private readonly byte _b1;
    private readonly byte _b2;
    private readonly byte _b3;
    private readonly byte _b4;
    private readonly byte _b5;

    /// <summary>Creates a <see cref="MacAddress"/> from exactly 6 bytes, most-significant octet first.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly <see cref="Length"/> bytes long.</exception>
    public MacAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A MAC address must be exactly {Length} bytes (got {bytes.Length}).", nameof(bytes));
        }

        _b0 = bytes[0];
        _b1 = bytes[1];
        _b2 = bytes[2];
        _b3 = bytes[3];
        _b4 = bytes[4];
        _b5 = bytes[5];
    }

    /// <summary>The Ethernet broadcast address <c>FF:FF:FF:FF:FF:FF</c>, used to reach all EtherCAT slaves.</summary>
    public static MacAddress Broadcast { get; } = new(stackalloc byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    /// <summary>Writes the 6 octets of this address into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Length"/>.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Destination span must be at least {Length} bytes.", nameof(destination));
        }

        destination[0] = _b0;
        destination[1] = _b1;
        destination[2] = _b2;
        destination[3] = _b3;
        destination[4] = _b4;
        destination[5] = _b5;
    }

    /// <summary>Returns the 6 octets of this address as a new array.</summary>
    public byte[] ToByteArray() => [_b0, _b1, _b2, _b3, _b4, _b5];

    /// <inheritdoc />
    public bool Equals(MacAddress other) =>
        _b0 == other._b0 && _b1 == other._b1 && _b2 == other._b2 &&
        _b3 == other._b3 && _b4 == other._b4 && _b5 == other._b5;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MacAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_b0, _b1, _b2, _b3, _b4, _b5);

    public static bool operator ==(MacAddress left, MacAddress right) => left.Equals(right);

    public static bool operator !=(MacAddress left, MacAddress right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"{_b0:X2}:{_b1:X2}:{_b2:X2}:{_b3:X2}:{_b4:X2}:{_b5:X2}";
}
