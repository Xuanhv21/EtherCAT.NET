using System.Buffers.Binary;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// Typed register-access client over the <see cref="EtherCAT.NET.Engine.Protocol"/> layer and an
/// <see cref="IEthernetFrameTransport"/>: builds one-datagram-per-exchange frames with
/// <see cref="EtherCatFrameBuilder"/>, sends them, waits for the matching reply (matched by command
/// + datagram index) parsed back with <see cref="EtherCatFrameParser"/>, and validates the returned
/// Working Counter against what the caller expects. A WKC mismatch — or no reply at all within
/// <see cref="ResponseTimeout"/> — always throws (<see cref="EscWorkingCounterException"/> or
/// <see cref="EscCommunicationException"/> respectively) rather than being silently ignored.
/// </summary>
/// <remarks>
/// One <see cref="EscClient"/> is not thread-safe for concurrent overlapping exchanges (it is built
/// for the strictly sequential request/reply usage of discovery, ESC configuration, and the AL state
/// machine); the cyclic PDO exchange (LRW) added in a later milestone step uses the Protocol layer
/// directly instead of going through this client.
/// </remarks>
public sealed class EscClient
{
    private readonly IEthernetFrameTransport _transport;
    private readonly MacAddress _source;
    private byte _nextIndex;

    /// <summary>
    /// How long <see cref="ReadRegister"/>/<see cref="WriteRegister"/>/etc. wait for a matching reply
    /// before throwing <see cref="EscCommunicationException"/>. Defaults to 200 ms; against a
    /// synchronous fake transport the reply is observed before <c>Send</c> even returns, so this
    /// timeout is only ever exercised by a real, asynchronous transport or a genuinely unresponsive
    /// slave.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Creates an <see cref="EscClient"/> that exchanges datagrams over <paramref name="transport"/>.</summary>
    /// <param name="transport">The transport to send frames on and receive replies from.</param>
    /// <param name="source">The source MAC address to stamp on every outgoing frame (the local NIC's own address, or an arbitrary locally-administered address for a fake transport).</param>
    public EscClient(IEthernetFrameTransport transport, MacAddress source)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _source = source;
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes starting at <paramref name="registerAddress"/> from the
    /// slave at <paramref name="stationAddress"/> via FPRD.
    /// </summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not <paramref name="expectedWorkingCounter"/> (default 1: exactly one slave answered).</exception>
    /// <exception cref="EscCommunicationException">No reply was observed within <see cref="ResponseTimeout"/>.</exception>
    public byte[] ReadRegister(ushort stationAddress, ushort registerAddress, int length, ushort expectedWorkingCounter = 1)
    {
        var reply = Exchange(EtherCatCommand.Fprd, stationAddress, registerAddress, new byte[length], expectedWorkingCounter);
        return reply.Data;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="registerAddress"/> on the slave at
    /// <paramref name="stationAddress"/> via FPWR.
    /// </summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not <paramref name="expectedWorkingCounter"/> (default 1: exactly one slave answered).</exception>
    /// <exception cref="EscCommunicationException">No reply was observed within <see cref="ResponseTimeout"/>.</exception>
    public void WriteRegister(ushort stationAddress, ushort registerAddress, ReadOnlySpan<byte> data, ushort expectedWorkingCounter = 1)
    {
        Exchange(EtherCatCommand.Fpwr, stationAddress, registerAddress, data.ToArray(), expectedWorkingCounter);
    }

    /// <summary>
    /// Broadcast-reads <paramref name="length"/> bytes starting at <paramref name="registerAddress"/>
    /// from every slave on the bus via BRD, OR-accumulated into a single result — the standard way to
    /// count slaves during discovery (e.g. reading 2 bytes from the Configured Station Address
    /// register and inspecting the returned Working Counter). Unlike <see cref="ReadRegister"/>, the
    /// WKC here is not validated against a fixed expectation (the number of slaves on the bus is
    /// exactly what the caller is trying to discover); it is returned as-is for the caller to inspect.
    /// </summary>
    /// <exception cref="EscCommunicationException">No reply was observed within <see cref="ResponseTimeout"/>.</exception>
    public (byte[] Data, ushort SlaveCount) BroadcastRead(ushort registerAddress, int length)
    {
        var reply = Exchange(EtherCatCommand.Brd, adp: 0, registerAddress, new byte[length], expectedWorkingCounter: null);
        return (reply.Data, reply.WorkingCounter);
    }

    /// <summary>
    /// Assigns a Configured Station Address to the slave at auto-increment position
    /// <paramref name="autoIncrementAddress"/> (the two's-complement ring position: 0 for the first
    /// slave, unchecked((ushort)-1) for the second, and so on) by writing
    /// <paramref name="stationAddress"/> to its Configured Station Address register (0x0010) via APWR.
    /// </summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1 (exactly one slave matched the auto-increment position).</exception>
    /// <exception cref="EscCommunicationException">No reply was observed within <see cref="ResponseTimeout"/>.</exception>
    public void ConfigureStationAddress(ushort autoIncrementAddress, ushort stationAddress)
    {
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, stationAddress);
        Exchange(EtherCatCommand.Apwr, autoIncrementAddress, EscRegisters.ConfiguredStationAddressRegister, data.ToArray(), expectedWorkingCounter: 1);
    }

    /// <summary>Reads the raw AL Control register (0x0120) of the slave at <paramref name="stationAddress"/>.</summary>
    public ushort ReadAlControl(ushort stationAddress) =>
        BinaryPrimitives.ReadUInt16LittleEndian(ReadRegister(stationAddress, EscRegisters.AlControlRegister, 2));

    /// <summary>Requests an AL state transition by writing <paramref name="state"/> to the AL Control register (0x0120).</summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1.</exception>
    public void WriteAlControl(ushort stationAddress, AlState state)
    {
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)state);
        WriteRegister(stationAddress, EscRegisters.AlControlRegister, data);
    }

    /// <summary>
    /// Reads and decodes the AL Status (0x0130) and AL Status Code (0x0134) registers of the slave at
    /// <paramref name="stationAddress"/>.
    /// </summary>
    /// <exception cref="EscWorkingCounterException">Either register read's WKC was not 1.</exception>
    public AlStatusReport ReadAlStatus(ushort stationAddress)
    {
        var rawStatus = BinaryPrimitives.ReadUInt16LittleEndian(ReadRegister(stationAddress, EscRegisters.AlStatusRegister, 2));
        var rawCode = BinaryPrimitives.ReadUInt16LittleEndian(ReadRegister(stationAddress, EscRegisters.AlStatusCodeRegister, 2));

        var state = (AlState)(rawStatus & 0x000F);
        var hasError = (rawStatus & 0x0010) != 0;

        return new AlStatusReport(state, hasError, new AlStatusCode(rawCode));
    }

    /// <summary>Writes FMMU configuration block <paramref name="index"/> (0-based) for the slave at <paramref name="stationAddress"/>.</summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1.</exception>
    public void WriteFmmuConfig(ushort stationAddress, int index, FmmuConfig config) =>
        WriteRegister(stationAddress, EscRegisters.FmmuAddress(index), config.ToBytes());

    /// <summary>Reads back FMMU configuration block <paramref name="index"/> (0-based) for the slave at <paramref name="stationAddress"/>.</summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1.</exception>
    public FmmuConfig ReadFmmuConfig(ushort stationAddress, int index) =>
        FmmuConfig.FromBytes(ReadRegister(stationAddress, EscRegisters.FmmuAddress(index), FmmuConfig.ByteLength));

    /// <summary>Writes Sync Manager configuration block <paramref name="index"/> (0-based) for the slave at <paramref name="stationAddress"/>.</summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1.</exception>
    public void WriteSmConfig(ushort stationAddress, int index, SmConfig config) =>
        WriteRegister(stationAddress, EscRegisters.SmAddress(index), config.ToBytes());

    /// <summary>Reads back Sync Manager configuration block <paramref name="index"/> (0-based) for the slave at <paramref name="stationAddress"/>.</summary>
    /// <exception cref="EscWorkingCounterException">The reply's WKC was not 1.</exception>
    public SmConfig ReadSmConfig(ushort stationAddress, int index) =>
        SmConfig.FromBytes(ReadRegister(stationAddress, EscRegisters.SmAddress(index), SmConfig.ByteLength));

    /// <summary>
    /// Sends one node-addressed datagram and blocks until a reply datagram with the same command and
    /// index is observed via <see cref="IEthernetFrameTransport.FrameReceived"/>, then validates its
    /// Working Counter.
    /// </summary>
    private EtherCatDatagram Exchange(EtherCatCommand command, ushort adp, ushort ado, byte[] data, ushort? expectedWorkingCounter)
    {
        var index = _nextIndex++;
        var request = new EtherCatDatagram(command, index, EtherCatAddress.ForNodeAddressed(adp, ado), data);
        var frame = EtherCatFrameBuilder.Build(MacAddress.Broadcast, _source, [request]);

        EtherCatDatagram? reply = null;
        using var replyReceived = new ManualResetEventSlim(initialState: false);

        void OnFrameReceived(object? sender, ReadOnlyMemory<byte> rawFrame)
        {
            EthernetFrame parsed;
            try
            {
                parsed = EtherCatFrameParser.Parse(rawFrame.Span);
            }
            catch
            {
                // Not a frame this client understands (e.g. some other EtherType); ignore it and
                // keep waiting for the reply we actually expect.
                return;
            }

            foreach (var candidate in parsed.Datagrams)
            {
                if (candidate.Command == command && candidate.Index == index)
                {
                    reply = candidate;
                    replyReceived.Set();
                    return;
                }
            }
        }

        _transport.FrameReceived += OnFrameReceived;
        try
        {
            _transport.Send(frame);

            if (!replyReceived.Wait(ResponseTimeout))
            {
                throw new EscCommunicationException(
                    $"Timed out after {ResponseTimeout.TotalMilliseconds:F0} ms waiting for a reply to {command} Adp=0x{adp:X4} Ado=0x{ado:X4}.");
            }
        }
        finally
        {
            _transport.FrameReceived -= OnFrameReceived;
        }

        var result = reply ?? throw new EscCommunicationException(
            $"No reply datagram matching {command} Adp=0x{adp:X4} Ado=0x{ado:X4} (index {index}) was observed.");

        if (expectedWorkingCounter is { } expected && result.WorkingCounter != expected)
        {
            throw new EscWorkingCounterException(command, adp, ado, expected, result.WorkingCounter);
        }

        return result;
    }
}
