using EtherCAT.NET.Engine.Protocol;

namespace EtherCAT.NET.Engine.Esc;

/// <summary>
/// Thrown by <see cref="EscClient"/> when a datagram's returned Working Counter does not match the
/// value the caller expected — the sole basis, per the implementation plan, for deciding whether a
/// register access actually reached (and was processed by) a slave. A bad WKC must never be
/// silently ignored, so every typed helper on <see cref="EscClient"/> that has a well-defined
/// expected WKC (FPRD/FPWR/APWR against exactly one slave) throws this instead of returning data
/// that may not reflect what happened on the wire.
/// </summary>
public sealed class EscWorkingCounterException : Exception
{
    /// <summary>The command whose datagram carried the unexpected Working Counter.</summary>
    public EtherCatCommand Command { get; }

    /// <summary>The node address (position or Configured Station Address) the datagram targeted.</summary>
    public ushort Adp { get; }

    /// <summary>The ESC register address the datagram targeted.</summary>
    public ushort Ado { get; }

    /// <summary>The Working Counter the caller required for this exchange to be considered successful.</summary>
    public ushort ExpectedWorkingCounter { get; }

    /// <summary>The Working Counter actually returned by the bus.</summary>
    public ushort ActualWorkingCounter { get; }

    /// <summary>Creates an <see cref="EscWorkingCounterException"/> describing a WKC mismatch.</summary>
    public EscWorkingCounterException(EtherCatCommand command, ushort adp, ushort ado, ushort expectedWorkingCounter, ushort actualWorkingCounter)
        : base(
            $"{command} to Adp=0x{adp:X4} Ado=0x{ado:X4} returned WKC={actualWorkingCounter}, expected {expectedWorkingCounter}. " +
            "The datagram may not have reached or been processed by the target slave.")
    {
        Command = command;
        Adp = adp;
        Ado = ado;
        ExpectedWorkingCounter = expectedWorkingCounter;
        ActualWorkingCounter = actualWorkingCounter;
    }
}

/// <summary>
/// Thrown by <see cref="EscClient"/> when no reply datagram matching a request was observed on
/// <see cref="EtherCAT.NET.Engine.Transport.IEthernetFrameTransport.FrameReceived"/> within the
/// configured <see cref="EscClient.ResponseTimeout"/> — the frame was dropped, no slave answered at
/// all, or the transport otherwise never produced a matching reply.
/// </summary>
public sealed class EscCommunicationException : Exception
{
    /// <summary>Creates an <see cref="EscCommunicationException"/> with the given message.</summary>
    public EscCommunicationException(string message) : base(message)
    {
    }
}
