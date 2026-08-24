using System.Buffers.Binary;

namespace EtherCAT.NET.Engine.Tests.Fakes;

/// <summary>
/// Hook invoked by <see cref="FakeSlaveDevice"/> whenever a write to the AL Control register
/// (0x0120) requests an AL state transition, letting a test decide whether to refuse it.
/// </summary>
/// <param name="previousState">The AL state (low 4 bits of AL Status, 0x0130) before this write.</param>
/// <param name="requestedState">The state being requested (low 4 bits of the newly-written AL Control value).</param>
/// <returns>
/// <c>null</c> to accept the transition normally: AL Status (0x0130) becomes
/// <paramref name="requestedState"/> and AL Status Code (0x0134) becomes 0 (no error). A non-null
/// value refuses the transition instead: AL Status keeps <paramref name="previousState"/> but with
/// the Error flag (bit 4, 0x0010) set, and AL Status Code is set to the returned value.
/// </returns>
public delegate ushort? AlTransitionRefusalHook(ushort previousState, ushort requestedState);

/// <summary>
/// A software model of one EtherCAT slave controller (ESC), used by <see cref="FakeBus"/> to give
/// the rest of the Engine something to talk to without any real hardware or Npcap. Backing storage
/// is a flat 64KB byte array standing in for the ESC's full register address space, so any register
/// read/write built by later steps (EscClient, discovery, FMMU/SM configuration, the state machine)
/// works against it without this class needing to know about them ahead of time. A handful of
/// addresses get reactive behaviour on top of that flat storage: AL Control (0x0120) drives an
/// internal AL state that updates AL Status (0x0130) / AL Status Code (0x0134), the SII Control
/// register (0x0502) resolves a read request against the flat <c>_siiEeprom</c> array (modelling no
/// EEPROM access latency, so <c>EscClient</c>/<c>SiiEeprom</c>'s busy-poll loop always sees the
/// result on its very first poll), and the FMMU config table (0x0600 + n*0x10) is consulted to
/// resolve logically-addressed (LRD/LWR/LRW) accesses against the physical memory a Sync Manager
/// was configured to occupy — everything else (SM config itself, and any other register) is simply
/// stored verbatim.
/// </summary>
public sealed class FakeSlaveDevice
{
    /// <summary>Size of the simulated ESC register address space.</summary>
    public const int RegisterSpaceLength = 65536;

    /// <summary>Size of the simulated SII EEPROM, in bytes (word-addressed, so this is 1024 words).</summary>
    public const int SiiEepromLength = 2048;

    /// <summary>Configured Station Address register, written via APWR during discovery.</summary>
    public const ushort ConfiguredStationAddressRegister = 0x0010;

    /// <summary>AL Control register.</summary>
    public const ushort AlControlRegister = 0x0120;

    /// <summary>AL Status register.</summary>
    public const ushort AlStatusRegister = 0x0130;

    /// <summary>AL Status Code register.</summary>
    public const ushort AlStatusCodeRegister = 0x0134;

    /// <summary>SII EEPROM Control/Status register (2 bytes). Bit 8 requests a read; bit 15 is Busy.</summary>
    public const ushort SiiControlRegister = 0x0502;

    /// <summary>SII EEPROM Address register (4 bytes) — the word address a read targets.</summary>
    public const ushort SiiAddressRegister = 0x0504;

    /// <summary>SII EEPROM Data register (4 bytes used here) — holds the result once a read resolves.</summary>
    public const ushort SiiDataRegister = 0x0508;

    /// <summary>SII word offset of the 4-byte Vendor Id field.</summary>
    public const ushort SiiVendorIdWordOffset = 0x0008;

    /// <summary>SII word offset of the 4-byte Product Code field.</summary>
    public const ushort SiiProductCodeWordOffset = 0x000A;

    /// <summary>SII word offset of the 4-byte Revision Number field.</summary>
    public const ushort SiiRevisionWordOffset = 0x000C;

    /// <summary>Base register address of FMMU0's 16-byte configuration block.</summary>
    public const ushort FmmuBaseRegister = 0x0600;

    /// <summary>Byte distance between consecutive FMMU configuration blocks.</summary>
    public const int FmmuStride = 0x10;

    /// <summary>Number of FMMU configuration blocks modelled (ESC-typical minimum).</summary>
    public const int FmmuCount = 3;

    /// <summary>Base register address of SM0's 8-byte configuration block.</summary>
    public const ushort SmBaseRegister = 0x0800;

    /// <summary>Byte distance between consecutive Sync Manager configuration blocks.</summary>
    public const int SmStride = 0x08;

    /// <summary>Number of Sync Manager configuration blocks modelled.</summary>
    public const int SmCount = 4;

    private const ushort AlStateMask = 0x000F;
    private const ushort AlErrorFlag = 0x0010;
    private const ushort SiiReadCommandBit = 0x0100;

    private readonly byte[] _registers = new byte[RegisterSpaceLength];
    private readonly byte[] _siiEeprom = new byte[SiiEepromLength];

    /// <summary>
    /// Creates a fake slave, optionally seeding its SII EEPROM identity fields (see
    /// <see cref="SeedSiiIdentity"/>) up front. Boots in AL state INIT (0x0001) with no AL error.
    /// </summary>
    public FakeSlaveDevice(uint vendorId = 0, uint productCode = 0, uint revisionNumber = 0)
    {
        SeedSiiIdentity(vendorId, productCode, revisionNumber);
        WriteAlStatusDirect(currentState: 0x0001, statusCode: 0x0000);
    }

    /// <summary>
    /// The slave's Configured Station Address — simply register <see cref="ConfiguredStationAddressRegister"/>
    /// read back, since that is exactly what an APWR to that register during discovery sets.
    /// </summary>
    public ushort ConfiguredStationAddress => ReadRegisterUInt16(ConfiguredStationAddressRegister);

    /// <summary>Current contents of the AL Control register (0x0120).</summary>
    public ushort AlControl => ReadRegisterUInt16(AlControlRegister);

    /// <summary>Current contents of the AL Status register (0x0130).</summary>
    public ushort AlStatus => ReadRegisterUInt16(AlStatusRegister);

    /// <summary>Current contents of the AL Status Code register (0x0134).</summary>
    public ushort AlStatusCode => ReadRegisterUInt16(AlStatusCodeRegister);

    /// <summary>
    /// When set, called every time a write to <see cref="AlControlRegister"/> requests a state
    /// transition, letting a test force that specific transition to be refused (and pick the AL
    /// Status Code it is refused with) instead of the default "always accept" behaviour.
    /// </summary>
    public AlTransitionRefusalHook? TransitionRefusal { get; set; }

    /// <summary>
    /// Writes the Vendor Id / Product Code / Revision Number fields of the simulated SII EEPROM at
    /// their standard word offsets (0x0008 / 0x000A / 0x000C respectively, each a 4-byte field).
    /// </summary>
    public void SeedSiiIdentity(uint vendorId, uint productCode, uint revisionNumber)
    {
        WriteSiiUInt32(SiiVendorIdWordOffset, vendorId);
        WriteSiiUInt32(SiiProductCodeWordOffset, productCode);
        WriteSiiUInt32(SiiRevisionWordOffset, revisionNumber);
    }

    /// <summary>Reads <paramref name="length"/> raw bytes out of the simulated SII EEPROM starting at byte offset <paramref name="byteOffset"/>.</summary>
    public byte[] ReadSiiBytes(ushort byteOffset, int length)
    {
        var result = new byte[length];
        _siiEeprom.AsSpan(byteOffset, length).CopyTo(result);
        return result;
    }

    /// <summary>Reads <paramref name="length"/> raw bytes out of the ESC register space starting at <paramref name="address"/>.</summary>
    public byte[] ReadRegisterBytes(ushort address, int length)
    {
        var result = new byte[length];
        ReadRegisterBytes(address, result);
        return result;
    }

    /// <summary>Reads register bytes starting at <paramref name="address"/> into <paramref name="destination"/>.</summary>
    public void ReadRegisterBytes(ushort address, Span<byte> destination)
    {
        if (address + destination.Length > RegisterSpaceLength)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address, "Read extends past the end of the 64KB register space.");
        }

        _registers.AsSpan(address, destination.Length).CopyTo(destination);
    }

    /// <summary>Convenience wrapper reading a single little-endian 16-bit register value.</summary>
    public ushort ReadRegisterUInt16(ushort address) => BinaryPrimitives.ReadUInt16LittleEndian(_registers.AsSpan(address, 2));

    /// <summary>
    /// Writes <paramref name="data"/> into the ESC register space starting at <paramref name="address"/>,
    /// storing it verbatim. If the write touches the AL Control register (0x0120), this additionally
    /// runs the AL state transition logic described on <see cref="TransitionRefusal"/>.
    /// </summary>
    public void WriteRegisterBytes(ushort address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (address + data.Length > RegisterSpaceLength)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address, "Write extends past the end of the 64KB register space.");
        }

        data.CopyTo(_registers.AsSpan(address, data.Length));

        if (Overlaps(address, data.Length, AlControlRegister, 2))
        {
            HandleAlControlWritten();
        }

        if (Overlaps(address, data.Length, SiiControlRegister, 2))
        {
            HandleSiiControlWritten();
        }
    }

    /// <summary>
    /// Resolves a logically-addressed read (LRD, or the read half of LRW) against this slave's
    /// enabled read FMMUs, OR-ing the physical bytes each matching FMMU maps into
    /// <paramref name="destination"/> at the corresponding offset.
    /// </summary>
    /// <returns><c>true</c> if at least one of this slave's FMMUs overlapped the requested range (i.e. the slave "processed" the datagram and should contribute to the WKC).</returns>
    public bool TryApplyLogicalRead(uint logicalAddress, Span<byte> destination)
    {
        var processed = false;
        for (var i = 0; i < FmmuCount; i++)
        {
            var fmmu = ReadFmmuMapping(i);
            if (!fmmu.Active || !fmmu.ReadEnabled)
            {
                continue;
            }

            if (!TryOverlap(fmmu, logicalAddress, destination.Length, out var bufferOffset, out var physicalOffset, out var count))
            {
                continue;
            }

            for (var b = 0; b < count; b++)
            {
                destination[bufferOffset + b] |= _registers[physicalOffset + b];
            }

            processed = true;
        }

        return processed;
    }

    /// <summary>
    /// Resolves a logically-addressed write (LWR, or the write half of LRW) against this slave's
    /// enabled write FMMUs, copying the relevant slice of <paramref name="source"/> into the
    /// physical memory each matching FMMU maps it to.
    /// </summary>
    /// <returns><c>true</c> if at least one of this slave's FMMUs overlapped the requested range.</returns>
    public bool TryApplyLogicalWrite(uint logicalAddress, ReadOnlySpan<byte> source)
    {
        var processed = false;
        for (var i = 0; i < FmmuCount; i++)
        {
            var fmmu = ReadFmmuMapping(i);
            if (!fmmu.Active || !fmmu.WriteEnabled)
            {
                continue;
            }

            if (!TryOverlap(fmmu, logicalAddress, source.Length, out var bufferOffset, out var physicalOffset, out var count))
            {
                continue;
            }

            source.Slice(bufferOffset, count).CopyTo(_registers.AsSpan(physicalOffset, count));
            processed = true;
        }

        return processed;
    }

    private void HandleAlControlWritten()
    {
        var previousState = (ushort)(ReadRegisterUInt16(AlStatusRegister) & AlStateMask);
        var requestedState = (ushort)(ReadRegisterUInt16(AlControlRegister) & AlStateMask);

        var refusalCode = TransitionRefusal?.Invoke(previousState, requestedState);

        if (refusalCode is ushort code)
        {
            WriteAlStatusDirect((ushort)(previousState | AlErrorFlag), code);
        }
        else
        {
            WriteAlStatusDirect(requestedState, 0x0000);
        }
    }

    /// <summary>
    /// Reacts to a write of the SII Control register (0x0502): when the read-request bit (bit 8)
    /// was set, resolves the read immediately — copying the 4-byte field at the word address
    /// currently held in the Address register (0x0504) out of the flat <c>_siiEeprom</c> array into
    /// the Data register (0x0508) — and clears Control back to 0 (not busy, no error), so
    /// <c>SiiEeprom</c>'s very first poll of the Busy bit already observes completion. No write
    /// path is modelled: nothing in this milestone writes to a slave's SII EEPROM.
    /// </summary>
    private void HandleSiiControlWritten()
    {
        var control = ReadRegisterUInt16(SiiControlRegister);
        if ((control & SiiReadCommandBit) == 0)
        {
            return;
        }

        var wordAddress = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(_registers.AsSpan(SiiAddressRegister, 4));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_siiEeprom.AsSpan(wordAddress * 2, 4));

        BinaryPrimitives.WriteUInt32LittleEndian(_registers.AsSpan(SiiDataRegister, 4), value);
        BinaryPrimitives.WriteUInt16LittleEndian(_registers.AsSpan(SiiControlRegister, 2), 0x0000);
    }

    private void WriteAlStatusDirect(ushort currentState, ushort statusCode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_registers.AsSpan(AlStatusRegister, 2), currentState);
        BinaryPrimitives.WriteUInt16LittleEndian(_registers.AsSpan(AlStatusCodeRegister, 2), statusCode);
    }

    private void WriteSiiUInt32(ushort wordOffset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_siiEeprom.AsSpan(wordOffset * 2, 4), value);

    private FmmuMapping ReadFmmuMapping(int index)
    {
        var baseAddress = FmmuBaseRegister + (index * FmmuStride);
        var logicalStart = BinaryPrimitives.ReadUInt32LittleEndian(_registers.AsSpan(baseAddress, 4));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(_registers.AsSpan(baseAddress + 4, 2));
        var physicalStart = BinaryPrimitives.ReadUInt16LittleEndian(_registers.AsSpan(baseAddress + 8, 2));
        var type = _registers[baseAddress + 12];
        var activate = _registers[baseAddress + 13];

        return new FmmuMapping(
            logicalStart,
            length,
            physicalStart,
            ReadEnabled: (type & 0x01) != 0,
            WriteEnabled: (type & 0x02) != 0,
            Active: (activate & 0x01) != 0);
    }

    private static bool TryOverlap(FmmuMapping fmmu, uint logicalAddress, int length, out int bufferOffset, out int physicalOffset, out int count)
    {
        var requestStart = (long)logicalAddress;
        var requestEnd = requestStart + length;
        var fmmuStart = (long)fmmu.LogicalStart;
        var fmmuEnd = fmmuStart + fmmu.Length;

        var overlapStart = Math.Max(requestStart, fmmuStart);
        var overlapEnd = Math.Min(requestEnd, fmmuEnd);

        if (overlapEnd <= overlapStart)
        {
            bufferOffset = 0;
            physicalOffset = 0;
            count = 0;
            return false;
        }

        bufferOffset = (int)(overlapStart - requestStart);
        physicalOffset = fmmu.PhysicalStart + (int)(overlapStart - fmmuStart);
        count = (int)(overlapEnd - overlapStart);
        return true;
    }

    private static bool Overlaps(int writeStart, int writeLength, int regStart, int regLength) =>
        writeStart < regStart + regLength && regStart < writeStart + writeLength;

    private readonly record struct FmmuMapping(uint LogicalStart, ushort Length, ushort PhysicalStart, bool ReadEnabled, bool WriteEnabled, bool Active);
}
