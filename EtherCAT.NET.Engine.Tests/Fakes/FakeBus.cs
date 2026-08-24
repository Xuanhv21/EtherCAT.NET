using EtherCAT.NET.Engine.Protocol;

namespace EtherCAT.NET.Engine.Tests.Fakes;

/// <summary>
/// A software EtherCAT segment: a list of <see cref="FakeSlaveDevice"/>s that a raw, already-built
/// Ethernet frame can be run against, producing the reply frame that would come back from a real
/// bus. Incoming frames are read with this Engine's own <see cref="EtherCatFrameParser"/> (reused
/// as-is, exactly like a real NIC driver handing bytes to the protocol layer), but the reply frame
/// is assembled by hand in <see cref="BuildReplyFrame"/> rather than by calling back into
/// <see cref="EtherCatFrameBuilder"/> — so a bug in that encoder cannot be silently "validated" by a
/// test that only ever round-trips through the same buggy code.
/// </summary>
public sealed class FakeBus
{
    private readonly List<FakeSlaveDevice> _slaves = [];

    /// <summary>The slaves currently on this bus, in ring position order (position 0 first).</summary>
    public IReadOnlyList<FakeSlaveDevice> Slaves => _slaves;

    /// <summary>Adds a slave to the end of the ring (its position is its index in <see cref="Slaves"/>).</summary>
    public void AddSlave(FakeSlaveDevice slave)
    {
        ArgumentNullException.ThrowIfNull(slave);
        _slaves.Add(slave);
    }

    /// <summary>
    /// Failure-injection hook: when <c>true</c>, <see cref="Process"/> returns <c>null</c> for every
    /// frame, as if it had been dropped on the wire and never came back to the master's NIC.
    /// </summary>
    public bool DropAllFrames { get; set; }

    /// <summary>
    /// Failure-injection hook: when set, called once per datagram after the bus has computed its
    /// normal Working Counter, letting a test substitute a different value (e.g. force 0 to
    /// simulate "nothing processed it", or a deliberately-wrong non-zero value) without having to
    /// touch the datagram-processing logic itself. Receives the request datagram and the naturally
    /// computed WKC; returns the WKC to actually place in the reply.
    /// </summary>
    public Func<EtherCatDatagram, ushort, ushort>? WorkingCounterOverride { get; set; }

    /// <summary>
    /// Runs one already-built raw Ethernet frame against every slave on the bus and returns the
    /// resulting reply frame, or <c>null</c> if <see cref="DropAllFrames"/> is set.
    /// </summary>
    public byte[]? Process(ReadOnlyMemory<byte> requestFrame)
    {
        if (DropAllFrames)
        {
            return null;
        }

        var parsed = EtherCatFrameParser.Parse(requestFrame.Span);

        var replies = new List<(EtherCatDatagram Request, byte[] Data, ushort Wkc)>(parsed.Datagrams.Count);
        foreach (var datagram in parsed.Datagrams)
        {
            var data = (byte[])datagram.Data.Clone();
            var wkc = ProcessDatagram(datagram, data);

            if (WorkingCounterOverride is not null)
            {
                wkc = WorkingCounterOverride(datagram, wkc);
            }

            replies.Add((datagram, data, wkc));
        }

        return BuildReplyFrame(parsed.Destination, parsed.Source, parsed.EtherType, replies);
    }

    private ushort ProcessDatagram(EtherCatDatagram datagram, byte[] data) => datagram.Command switch
    {
        EtherCatCommand.Brd => ProcessBroadcastRead(datagram.Address.Ado, data),
        EtherCatCommand.Apwr => ProcessAutoIncrementWrite(datagram.Address.Adp, datagram.Address.Ado, data),
        EtherCatCommand.Fprd => ProcessConfiguredRead(datagram.Address.Adp, datagram.Address.Ado, data),
        EtherCatCommand.Fpwr => ProcessConfiguredWrite(datagram.Address.Adp, datagram.Address.Ado, data),
        EtherCatCommand.Lrd => ProcessLogicalRead(datagram.Address.LogicalAddress, data),
        EtherCatCommand.Lwr => ProcessLogicalWrite(datagram.Address.LogicalAddress, data),
        EtherCatCommand.Lrw => ProcessLogicalReadWrite(datagram.Address.LogicalAddress, data),

        // Nop and every auto-increment/broadcast/configured variant not needed by the plan
        // (Aprd, Aprw, Bwr, Brw, Frmw, Armw) are intentionally left unmodelled: no slave "processes"
        // them, so the datagram comes back with WKC=0 and its data untouched, exactly as if no
        // slave on the bus recognised the command.
        _ => 0,
    };

    private ushort ProcessBroadcastRead(ushort ado, byte[] data)
    {
        ushort wkc = 0;
        var accumulated = new byte[data.Length];
        var buffer = new byte[data.Length];

        foreach (var slave in _slaves)
        {
            slave.ReadRegisterBytes(ado, buffer);
            for (var i = 0; i < buffer.Length; i++)
            {
                accumulated[i] |= buffer[i];
            }

            wkc++;
        }

        accumulated.CopyTo(data, 0);
        return wkc;
    }

    private ushort ProcessAutoIncrementWrite(ushort adp, ushort ado, byte[] data)
    {
        for (var position = 0; position < _slaves.Count; position++)
        {
            if (unchecked((ushort)(adp + position)) != 0)
            {
                continue;
            }

            _slaves[position].WriteRegisterBytes(ado, data);
            return 1;
        }

        return 0;
    }

    private ushort ProcessConfiguredRead(ushort adp, ushort ado, byte[] data)
    {
        foreach (var slave in _slaves)
        {
            if (slave.ConfiguredStationAddress != adp)
            {
                continue;
            }

            slave.ReadRegisterBytes(ado, data);
            return 1;
        }

        return 0;
    }

    private ushort ProcessConfiguredWrite(ushort adp, ushort ado, byte[] data)
    {
        foreach (var slave in _slaves)
        {
            if (slave.ConfiguredStationAddress != adp)
            {
                continue;
            }

            slave.WriteRegisterBytes(ado, data);
            return 1;
        }

        return 0;
    }

    private ushort ProcessLogicalRead(uint logicalAddress, byte[] data)
    {
        // Real hardware relies on the master having zeroed the region it's about to LRD into; we
        // make that explicit here so OR-accumulation across slaves is well-defined regardless of
        // whatever bytes a caller happened to put in the request datagram.
        Array.Clear(data);

        ushort wkc = 0;
        foreach (var slave in _slaves)
        {
            if (slave.TryApplyLogicalRead(logicalAddress, data))
            {
                wkc++;
            }
        }

        return wkc;
    }

    private ushort ProcessLogicalWrite(uint logicalAddress, byte[] data)
    {
        ushort wkc = 0;
        foreach (var slave in _slaves)
        {
            if (slave.TryApplyLogicalWrite(logicalAddress, data))
            {
                wkc++;
            }
        }

        return wkc;
    }

    private ushort ProcessLogicalReadWrite(uint logicalAddress, byte[] data)
    {
        // Real ESCs process the write-enabled FMMUs and the read-enabled FMMUs independently, each
        // contributing up to +1 to the WKC per slave (so one slave with both an output and an input
        // FMMU configured contributes 2) -- write phase first, then read, matching the RxPDO/TxPDO
        // layout (outputs at the start of the datagram, inputs after).
        ushort wkc = 0;

        foreach (var slave in _slaves)
        {
            if (slave.TryApplyLogicalWrite(logicalAddress, data))
            {
                wkc++;
            }
        }

        foreach (var slave in _slaves)
        {
            if (slave.TryApplyLogicalRead(logicalAddress, data))
            {
                wkc++;
            }
        }

        return wkc;
    }

    /// <summary>
    /// Hand-encodes a reply frame in the exact wire format <see cref="EtherCatFrameBuilder"/> also
    /// produces (Ethernet header, 2-byte EtherCAT header, back-to-back datagrams with a correctly
    /// computed More bit, zero padding up to the Ethernet minimum frame length) but written
    /// independently of it, byte by byte, so this bus's replies are not merely an echo of whatever
    /// the builder under test happens to do.
    /// </summary>
    private static byte[] BuildReplyFrame(
        MacAddress destination,
        MacAddress source,
        ushort etherType,
        List<(EtherCatDatagram Request, byte[] Data, ushort Wkc)> replies)
    {
        var ecatPayloadLength = 0;
        foreach (var reply in replies)
        {
            ecatPayloadLength += EtherCatDatagram.HeaderLength + reply.Data.Length + EtherCatDatagram.WorkingCounterLength;
        }

        var unpaddedLength = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength + ecatPayloadLength;
        var frameLength = Math.Max(unpaddedLength, EthernetFrame.MinimumFrameLength);

        // Zero-initialized by the runtime -- exactly the padding needed beyond unpaddedLength.
        var frame = new byte[frameLength];

        destination.WriteTo(frame.AsSpan(0, 6));
        source.WriteTo(frame.AsSpan(6, 6));

        // EtherType is transmitted big-endian, like every other standard Ethernet II field.
        frame[12] = (byte)(etherType >> 8);
        frame[13] = (byte)etherType;

        var headerWord = (ushort)((ecatPayloadLength & 0x07FF) | (EthernetFrame.EtherCatHeaderTypeCommand << 12));
        frame[14] = (byte)headerWord;
        frame[15] = (byte)(headerWord >> 8);

        var offset = EthernetFrame.HeaderLength + EthernetFrame.EtherCatHeaderLength;
        for (var i = 0; i < replies.Count; i++)
        {
            var (request, data, wkc) = replies[i];
            var more = i < replies.Count - 1;

            frame[offset + 0] = (byte)request.Command;
            frame[offset + 1] = request.Index;
            frame[offset + 2] = (byte)request.Address.Adp;
            frame[offset + 3] = (byte)(request.Address.Adp >> 8);
            frame[offset + 4] = (byte)request.Address.Ado;
            frame[offset + 5] = (byte)(request.Address.Ado >> 8);

            var lenWord = (ushort)(data.Length & 0x07FF);
            if (more)
            {
                lenWord |= 0x8000;
            }

            frame[offset + 6] = (byte)lenWord;
            frame[offset + 7] = (byte)(lenWord >> 8);
            frame[offset + 8] = (byte)request.Irq;
            frame[offset + 9] = (byte)(request.Irq >> 8);

            data.CopyTo(frame, offset + EtherCatDatagram.HeaderLength);

            var wkcOffset = offset + EtherCatDatagram.HeaderLength + data.Length;
            frame[wkcOffset] = (byte)wkc;
            frame[wkcOffset + 1] = (byte)(wkc >> 8);

            offset = wkcOffset + EtherCatDatagram.WorkingCounterLength;
        }

        return frame;
    }
}
