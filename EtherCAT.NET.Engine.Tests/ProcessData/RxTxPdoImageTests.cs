using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;

namespace EtherCAT.NET.Engine.Tests.ProcessData;

/// <summary>
/// Round-trips every named <see cref="RxPdoImage"/>/<see cref="TxPdoImage"/> accessor through the
/// process-image plan computed from the real embedded MADLN01BE device, and asserts each one reads
/// and writes at the exact byte offset the plan's table specifies — never a magic offset.
/// </summary>
public class RxTxPdoImageTests
{
    private static ProcessImagePlan BuildPlan()
    {
        var library = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();
        var device = library.Devices.Single(d =>
            d.Name == "MADLN01BE" &&
            d.ProductCode == 0x60380000 &&
            d.RevisionNumber == 0x00010000);

        return ProcessImageBuilder.BuildDefault(device);
    }

    [Fact]
    public void RxPdoImage_properties_read_and_write_at_the_expected_byte_offsets()
    {
        var plan = BuildPlan();
        var rx = new RxPdoImage(plan.RxPdoLayout);

        rx.Controlword = 0x0F06;
        rx.ModesOfOperation = 0;
        rx.TargetPosition = -12345;
        rx.TouchProbeFunction = 0x0003;

        Assert.Equal(9, rx.Buffer.Length);
        Assert.Equal(0x0F06, rx.Controlword);
        Assert.Equal(0, (int)rx.ModesOfOperation);
        Assert.Equal(-12345, rx.TargetPosition);
        Assert.Equal(0x0003, rx.TouchProbeFunction);

        // Offsets per the plan table: Controlword@0(2B), Modes@2(1B), Target position@3(4B), Touch probe function@7(2B).
        Assert.Equal(0x06, rx.Buffer[0]);
        Assert.Equal(0x0F, rx.Buffer[1]);
        Assert.Equal(0, rx.Buffer[2]);
        Assert.Equal(-12345, BitConverter.ToInt32(rx.Buffer, 3));
        Assert.Equal(0x0003, BitConverter.ToUInt16(rx.Buffer, 7));
    }

    [Fact]
    public void TxPdoImage_properties_read_at_the_expected_byte_offsets()
    {
        var plan = BuildPlan();
        var buffer = new byte[plan.TxPdoLayout.TotalByteLength];

        WriteUInt16(buffer, 0, 0x0000);       // Error code
        WriteUInt16(buffer, 2, 0x0637);       // Statusword
        buffer[4] = 0;                        // Modes of operation display
        WriteInt32(buffer, 5, 424242);        // Position actual value
        WriteUInt16(buffer, 9, 0x0001);       // Touch probe status
        WriteInt32(buffer, 11, -99);          // Touch probe pos1
        WriteInt32(buffer, 15, 7);            // Following error actual value
        WriteUInt32(buffer, 19, 0xA5A5A5A5);  // Digital inputs

        var tx = new TxPdoImage(plan.TxPdoLayout, buffer);

        Assert.Equal(0x0000, tx.ErrorCode);
        Assert.Equal(0x0637, tx.Statusword);
        Assert.Equal(0, (int)tx.ModesOfOperationDisplay);
        Assert.Equal(424242, tx.PositionActualValue);
        Assert.Equal(0x0001, tx.TouchProbeStatus);
        Assert.Equal(-99, tx.TouchProbePos1);
        Assert.Equal(7, tx.FollowingErrorActualValue);
        Assert.Equal(0xA5A5A5A5u, tx.DigitalInputs);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
