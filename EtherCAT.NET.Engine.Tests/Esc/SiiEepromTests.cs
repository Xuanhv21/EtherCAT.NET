using System.Buffers.Binary;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.Transport;

namespace EtherCAT.NET.Engine.Tests.Esc;

/// <summary>
/// <see cref="SiiEeprom"/>'s busy-bit-polling word read sequence, exercised against
/// <see cref="SiiRegisterFakeTransport"/> -- a minimal register-mapped ESC model (distinct from
/// <c>Fakes/FakeSlaveDevice</c>, which stores the SII EEPROM as a flat byte array rather than wiring
/// it up behind the 0x0502/0x0504/0x0508 register interface) that models exactly the Control/Address/
/// Data protocol <see cref="SiiEeprom"/> drives, including a configurable number of "still busy"
/// polls before the result becomes available.
/// </summary>
public class SiiEepromTests
{
    private static readonly MacAddress TestSourceMac = new(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });
    private const ushort StationAddress = 0x1001;

    [Fact]
    public void ReadUInt32_returns_the_seeded_word_when_the_busy_bit_clears_immediately()
    {
        var transport = new SiiRegisterFakeTransport(StationAddress) { BusyPollsBeforeReady = 0 };
        transport.SeedWord(SiiEeprom.VendorIdWordAddress, 0x0000_1234u);

        var sii = new SiiEeprom(new EscClient(transport, TestSourceMac), StationAddress);

        Assert.Equal(0x0000_1234u, sii.ReadVendorId());
    }

    [Fact]
    public void ReadUInt32_polls_through_several_busy_replies_before_returning_the_result()
    {
        var transport = new SiiRegisterFakeTransport(StationAddress) { BusyPollsBeforeReady = 5 };
        transport.SeedWord(SiiEeprom.ProductCodeWordAddress, 0xCAFEF00Du);

        var sii = new SiiEeprom(new EscClient(transport, TestSourceMac), StationAddress);

        Assert.Equal(0xCAFEF00Du, sii.ReadProductCode());
        Assert.Equal(0, transport.RemainingBusyPolls); // fully drained, not just happened to succeed early.
    }

    [Fact]
    public void ReadUInt32_reads_the_correct_word_address_for_each_named_identity_field()
    {
        var transport = new SiiRegisterFakeTransport(StationAddress);
        transport.SeedWord(SiiEeprom.VendorIdWordAddress, 111);
        transport.SeedWord(SiiEeprom.ProductCodeWordAddress, 222);
        transport.SeedWord(SiiEeprom.RevisionNumberWordAddress, 333);
        transport.SeedWord(SiiEeprom.SerialNumberWordAddress, 444);

        var sii = new SiiEeprom(new EscClient(transport, TestSourceMac), StationAddress);

        Assert.Equal(111u, sii.ReadVendorId());
        Assert.Equal(222u, sii.ReadProductCode());
        Assert.Equal(333u, sii.ReadRevisionNumber());
        Assert.Equal(444u, sii.ReadSerialNumber());
    }

    [Fact]
    public void ReadUInt32_throws_TimeoutException_when_the_busy_bit_never_clears_within_MaxPollAttempts()
    {
        var transport = new SiiRegisterFakeTransport(StationAddress) { BusyPollsBeforeReady = 1000 };
        var sii = new SiiEeprom(new EscClient(transport, TestSourceMac), StationAddress) { MaxPollAttempts = 3 };

        Assert.Throws<TimeoutException>(() => sii.ReadVendorId());
    }

    /// <summary>
    /// A tiny <see cref="IEthernetFrameTransport"/> double modelling only what <see cref="SiiEeprom"/>
    /// touches: the SII Control (0x0502) / Address (0x0504) / Data (0x0508) registers of a single
    /// station address, with a configurable count of "still busy" responses before a triggered read
    /// resolves and the requested word becomes visible in the Data register.
    /// </summary>
    private sealed class SiiRegisterFakeTransport : IEthernetFrameTransport
    {
        private const ushort ReadCommandBit = 0x0100;
        private const ushort BusyBit = 0x8000;

        private readonly ushort _stationAddress;
        private readonly byte[] _registers = new byte[65536];
        private readonly Dictionary<ushort, uint> _eeprom = [];

        public SiiRegisterFakeTransport(ushort stationAddress) => _stationAddress = stationAddress;

        public string Name => "SiiRegisterFake";

        public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

        /// <summary>Number of "still busy" poll replies to give before a triggered read resolves.</summary>
        public int BusyPollsBeforeReady { get; set; }

        /// <summary>How many busy replies are still owed for the in-flight read, for tests to assert full drainage.</summary>
        public int RemainingBusyPolls { get; private set; }

        /// <summary>Seeds the value <see cref="SiiEeprom.ReadUInt32"/> should observe for <paramref name="wordAddress"/>.</summary>
        public void SeedWord(ushort wordAddress, uint value) => _eeprom[wordAddress] = value;

        public void Send(ReadOnlyMemory<byte> frame)
        {
            var parsed = EtherCatFrameParser.Parse(frame.Span);
            var request = parsed.Datagrams[0];
            var data = (byte[])request.Data.Clone();
            ushort wkc = 0;

            if (request.Address.Adp == _stationAddress)
            {
                wkc = 1;
                switch (request.Command)
                {
                    case EtherCatCommand.Fpwr:
                        HandleWrite(request.Address.Ado, data);
                        break;
                    case EtherCatCommand.Fprd:
                        HandleRead(request.Address.Ado, data);
                        break;
                }
            }

            var replyDatagram = new EtherCatDatagram(request.Command, request.Index, request.Address, data, request.Irq, wkc);
            var replyFrame = EtherCatFrameBuilder.Build(parsed.Source, parsed.Destination, [replyDatagram]);
            FrameReceived?.Invoke(this, replyFrame);
        }

        private void HandleWrite(ushort address, byte[] data)
        {
            data.CopyTo(_registers, address);

            if (address == EscRegisters.SiiControlRegister)
            {
                var control = BinaryPrimitives.ReadUInt16LittleEndian(data);
                if ((control & ReadCommandBit) != 0)
                {
                    RemainingBusyPolls = BusyPollsBeforeReady;
                }
            }
        }

        private void HandleRead(ushort address, byte[] data)
        {
            if (address == EscRegisters.SiiControlRegister)
            {
                ushort status;
                if (RemainingBusyPolls > 0)
                {
                    RemainingBusyPolls--;
                    status = BusyBit;
                }
                else
                {
                    status = 0x0000;

                    var wordAddress = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(_registers.AsSpan(EscRegisters.SiiAddressRegister, 4));
                    var value = _eeprom.GetValueOrDefault(wordAddress);
                    BinaryPrimitives.WriteUInt32LittleEndian(_registers.AsSpan(EscRegisters.SiiDataRegister, 4), value);
                }

                BinaryPrimitives.WriteUInt16LittleEndian(data, status);
            }
            else
            {
                _registers.AsSpan(address, data.Length).CopyTo(data);
            }
        }

        public void Dispose()
        {
        }
    }
}
