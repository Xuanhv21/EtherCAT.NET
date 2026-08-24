using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IEthernetFrameTransport"/> that wraps a <see cref="FakeBus"/> instead
/// of a real NIC. <see cref="Send"/> hands the frame straight to the bus and, synchronously and
/// deterministically (no background thread, no real I/O), raises <see cref="FrameReceived"/> with
/// whatever reply the bus computed -- letting every layer built on top of
/// <see cref="IEthernetFrameTransport"/> be unit-tested without Npcap or hardware.
/// </summary>
public sealed class FakeEthernetFrameTransport : IEthernetFrameTransport
{
    private readonly FakeBus _bus;
    private bool _disposed;

    /// <summary>Wraps <paramref name="bus"/> so it can be driven through <see cref="IEthernetFrameTransport"/>.</summary>
    public FakeEthernetFrameTransport(FakeBus bus, string name = "Fake")
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>
    /// Runs <paramref name="frame"/> against the wrapped bus and, unless the bus dropped it (see
    /// <see cref="FakeBus.DropAllFrames"/>), immediately raises <see cref="FrameReceived"/> with the
    /// reply, on the calling thread.
    /// </summary>
    public void Send(ReadOnlyMemory<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var reply = _bus.Process(frame);
        if (reply is not null)
        {
            FrameReceived?.Invoke(this, reply);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
