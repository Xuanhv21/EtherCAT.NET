using EtherCAT.NET.Engine.Esc;

namespace EtherCAT.NET.Engine.ProcessData;

/// <summary>
/// One cycle's worth of observable state, published by <see cref="CyclicExchangeService.StatusUpdated"/>.
/// Carries the decoded Statusword plus everything a UI needs to render freshness/health without
/// re-deriving it: the raw Working Counter, whether this cycle's data is trustworthy, the last
/// error message (if any), and a monotonically increasing sequence number a UI can use to detect
/// gaps or reordering.
/// </summary>
/// <param name="SequenceNumber">Monotonically increasing per-cycle counter, starting at 1 on the first cycle.</param>
/// <param name="RawStatusword">The raw Statusword (0x6041) value from the most recent cycle whose data was fresh (retained/stale across a failed cycle — see <paramref name="IsDataFresh"/>).</param>
/// <param name="Status">Bit-decoded view of <paramref name="RawStatusword"/>.</param>
/// <param name="AlState">The AL state last reported to this service via <see cref="CyclicExchangeService.SetAlState"/> — this service never reads the AL Status register itself.</param>
/// <param name="LastWkc">The Working Counter returned by this cycle's LRW exchange (0 if no reply was observed at all).</param>
/// <param name="IsDataFresh"><c>false</c> when this cycle's LRW exchange failed (WKC mismatch or no reply) — <paramref name="RawStatusword"/>/<paramref name="Status"/> then reflect the last cycle that did succeed, not this one.</param>
/// <param name="LastError">Human-readable description of the most recent failure, or <c>null</c> while healthy.</param>
/// <param name="IsFaulted"><c>true</c> once consecutive failures exceeded <see cref="CyclicExchangeService.MaxConsecutiveFailures"/> and the loop has stopped itself.</param>
public sealed record ProcessImageSnapshot(
    long SequenceNumber,
    ushort RawStatusword,
    Ds402Statusword Status,
    AlState AlState,
    ushort LastWkc,
    bool IsDataFresh,
    string? LastError,
    bool IsFaulted);
