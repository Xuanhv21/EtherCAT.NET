namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// The decoded contents of a slave's AL Status (0x0130) + AL Status Code (0x0134) registers, as
/// returned by <see cref="EscClient.ReadAlStatus"/>.
/// </summary>
/// <param name="State">The AL state currently reported by the slave (AL Status, low 4 bits).</param>
/// <param name="HasError">The AL Status Error flag (bit 4, 0x0010): <c>true</c> when the slave refused the last requested transition.</param>
/// <param name="StatusCode">
/// AL Status Code (0x0134). Only meaningful when <paramref name="HasError"/> is <c>true</c>; a
/// well-behaved slave reports <see cref="AlStatusCode.NoError"/> otherwise, but callers should gate
/// on <see cref="HasError"/> rather than on this value being non-zero.
/// </param>
public readonly record struct AlStatusReport(AlState State, bool HasError, AlStatusCode StatusCode);
