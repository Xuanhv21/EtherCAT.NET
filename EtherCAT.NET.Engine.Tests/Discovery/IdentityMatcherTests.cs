using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Tests.Discovery;

/// <summary>
/// <see cref="IdentityMatcher"/> against the real, embedded, fully parsed Panasonic MINAS A6BE ESI
/// library (not a fabricated ESI model) -- the happy path picking out MADLN01BE by exact Product
/// Code + Revision Number, the Vendor Id cross-check, and the not-found path producing a clear,
/// loud exception rather than a crash or a silent null.
/// </summary>
public class IdentityMatcherTests
{
    private const uint PanasonicVendorId = 0x066F;
    private const uint Madln01BeProductCode = 0x60380000;
    private const uint Madln01BeRevision = 0x00010000;

    private static EsiDeviceLibrary RealLibrary() => EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();

    [Fact]
    public void Match_picks_MADLN01BE_by_exact_ProductCode_and_RevisionNumber_out_of_the_real_library()
    {
        var identity = new SlaveIdentity(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);

        var device = IdentityMatcher.Match(identity, RealLibrary());

        Assert.Equal("MADLN01BE", device.Name);
        Assert.Equal(Madln01BeProductCode, device.ProductCode);
        Assert.Equal(Madln01BeRevision, device.RevisionNumber);
    }

    [Fact]
    public void Match_never_returns_null_on_the_happy_path()
    {
        var identity = new SlaveIdentity(PanasonicVendorId, Madln01BeProductCode, Madln01BeRevision);

        var device = IdentityMatcher.Match(identity, RealLibrary());

        Assert.NotNull(device);
    }

    [Fact]
    public void Match_throws_a_clear_error_for_a_ProductCode_that_matches_no_device_instead_of_crashing_or_returning_null()
    {
        const uint bogusProductCode = 0xDEADBEEF;
        var bogusIdentity = new SlaveIdentity(PanasonicVendorId, bogusProductCode, Madln01BeRevision);

        var ex = Assert.Throws<SlaveIdentityMismatchException>(() => IdentityMatcher.Match(bogusIdentity, RealLibrary()));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Contains("DEADBEEF", ex.Message);
    }

    [Fact]
    public void Match_throws_a_clear_error_when_the_Vendor_Id_does_not_match_the_library()
    {
        const uint wrongVendorId = 0x00000099;
        var wrongVendorIdentity = new SlaveIdentity(wrongVendorId, Madln01BeProductCode, Madln01BeRevision);

        var ex = Assert.Throws<SlaveIdentityMismatchException>(() => IdentityMatcher.Match(wrongVendorIdentity, RealLibrary()));

        Assert.Contains("Vendor Id", ex.Message);
    }
}
