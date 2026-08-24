namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// The AL Status Code register (0x0134): a slave-supplied diagnostic code explaining why an AL
/// state transition failed (alongside the Error flag, bit 4 of AL Status). Wraps the raw 16-bit
/// code with a human-readable <see cref="Description"/> lookup covering the common codes from
/// ETG.1000.6; this table is deliberately not exhaustive — vendor-specific and less common codes
/// fall back to a generic "unknown" description rather than throwing, since a diagnostic helper
/// must never itself be the reason a caller cannot see the raw code.
/// </summary>
public readonly struct AlStatusCode : IEquatable<AlStatusCode>
{
    /// <summary>0x0000 — no error; the requested transition succeeded.</summary>
    public static readonly AlStatusCode NoError = new(0x0000);

    private static readonly Dictionary<ushort, string> Descriptions = new()
    {
        [0x0000] = "No error",
        [0x0001] = "Unspecified error",
        [0x0002] = "No memory",
        [0x0011] = "Invalid requested state change",
        [0x0012] = "Unknown requested state",
        [0x0013] = "Bootstrap not supported",
        [0x0014] = "No valid firmware",
        [0x0015] = "Invalid mailbox configuration (PreOP)",
        [0x0016] = "Invalid mailbox configuration (SafeOP)",
        [0x0017] = "Invalid sync manager configuration",
        [0x0018] = "No valid inputs available",
        [0x0019] = "No valid outputs available",
        [0x001A] = "Synchronization error",
        [0x001B] = "Sync manager watchdog",
        [0x001C] = "Invalid sync manager types",
        [0x001E] = "Invalid input configuration",
        [0x001F] = "Invalid watchdog configuration",
        [0x0020] = "Slave needs cold start",
        [0x0021] = "Slave needs INIT",
        [0x0022] = "Slave needs PREOP",
        [0x0023] = "Slave needs SAFEOP",
        [0x0024] = "Invalid output configuration",
    };

    /// <summary>The raw 16-bit code as reported by the slave.</summary>
    public ushort Value { get; }

    /// <summary>Wraps a raw AL Status Code value.</summary>
    public AlStatusCode(ushort value) => Value = value;

    /// <summary>
    /// A human-readable description of <see cref="Value"/>, or a generic "unknown" message
    /// (including the raw hex value) for codes not in the built-in table.
    /// </summary>
    public string Description =>
        Descriptions.TryGetValue(Value, out var text)
            ? text
            : $"Unknown or vendor-specific AL Status Code (0x{Value:X4})";

    /// <inheritdoc />
    public bool Equals(AlStatusCode other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AlStatusCode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(AlStatusCode left, AlStatusCode right) => left.Equals(right);

    public static bool operator !=(AlStatusCode left, AlStatusCode right) => !left.Equals(right);

    /// <summary>Implicitly wraps a raw register value as an <see cref="AlStatusCode"/>.</summary>
    public static implicit operator AlStatusCode(ushort value) => new(value);

    /// <summary>Implicitly unwraps the raw register value.</summary>
    public static implicit operator ushort(AlStatusCode code) => code.Value;

    /// <inheritdoc />
    public override string ToString() => $"0x{Value:X4} ({Description})";
}
