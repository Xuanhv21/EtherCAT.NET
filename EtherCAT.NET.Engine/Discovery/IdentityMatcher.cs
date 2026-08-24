using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Discovery;

/// <summary>
/// Finds the <see cref="EsiDeviceDescriptor"/> in a parsed <see cref="EsiDeviceLibrary"/> that
/// matches a slave's discovered <see cref="SlaveIdentity"/>, by exact Product Code + Revision
/// Number (also cross-checking the library's own <see cref="EsiVendor.Id"/> against the discovered
/// Vendor Id). Never returns a silent <c>null</c> on a mismatch — see
/// <see cref="SlaveIdentityMismatchException"/>.
/// </summary>
public static class IdentityMatcher
{
    /// <summary>
    /// Matches <paramref name="identity"/> against <paramref name="library"/>.
    /// </summary>
    /// <exception cref="SlaveIdentityMismatchException">
    /// <paramref name="identity"/>'s Vendor Id does not equal <paramref name="library"/>'s
    /// <see cref="EsiVendor.Id"/>, no device's Product Code + Revision Number matches, or (in a
    /// malformed ESI file) more than one device shares that same Product Code + Revision Number.
    /// </exception>
    public static EsiDeviceDescriptor Match(SlaveIdentity identity, EsiDeviceLibrary library)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(library);

        if (identity.VendorId != library.Vendor.Id)
        {
            throw new SlaveIdentityMismatchException(
                $"Discovered slave Vendor Id 0x{identity.VendorId:X8} does not match the ESI library's " +
                $"vendor '{library.Vendor.Name}' (0x{library.Vendor.Id:X8}). Wrong ESI file for this slave?");
        }

        var matches = library.Devices
            .Where(d => d.ProductCode == identity.ProductCode && d.RevisionNumber == identity.RevisionNumber)
            .ToList();

        if (matches.Count == 0)
        {
            var available = library.Devices.Count == 0
                ? "(the ESI library declares no devices at all)"
                : string.Join(", ", library.Devices.Select(d => $"{d.Name} (ProductCode=0x{d.ProductCode:X8}, Revision=0x{d.RevisionNumber:X8})"));

            throw new SlaveIdentityMismatchException(
                "No device in the ESI library matches the discovered slave " +
                $"(VendorId=0x{identity.VendorId:X8}, ProductCode=0x{identity.ProductCode:X8}, Revision=0x{identity.RevisionNumber:X8}). " +
                $"Devices available in the library: {available}.");
        }

        if (matches.Count > 1)
        {
            throw new SlaveIdentityMismatchException(
                $"{matches.Count} devices in the ESI library share ProductCode=0x{identity.ProductCode:X8} " +
                $"Revision=0x{identity.RevisionNumber:X8} ({string.Join(", ", matches.Select(d => d.Name))}); " +
                "the ESI file is ambiguous for this slave.");
        }

        return matches[0];
    }
}
