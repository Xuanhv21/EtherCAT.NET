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

    /// <summary>
    /// Matches <paramref name="identity"/> against a whole set of ESI libraries at once -- the
    /// multi-vendor counterpart to <see cref="Match"/>, for a catalog folder that may hold ESI
    /// files from several different vendors simultaneously (see <see cref="Esi.EsiCatalog"/>).
    /// Libraries whose <see cref="EsiVendor.Id"/> does not equal <paramref name="identity"/>'s
    /// Vendor Id are skipped rather than causing a throw, so an unrelated vendor's ESI file sitting
    /// in the same folder is simply ignored. Internally reuses <see cref="Match"/> per matching-vendor
    /// library (catching its exception when that library's Product Code + Revision Number does not
    /// match) so the per-library matching logic is never duplicated.
    /// </summary>
    /// <param name="identity">The discovered slave's SII identity fields.</param>
    /// <param name="libraries">Every ESI library available to search, e.g. <see cref="Esi.EsiCatalog.Libraries"/>.</param>
    /// <exception cref="SlaveIdentityMismatchException">
    /// No library among <paramref name="libraries"/> has a matching Vendor Id at all, no device
    /// across the vendor-matching libraries has a matching Product Code + Revision Number, or more
    /// than one device (possibly in different files) does.
    /// </exception>
    public static EsiDeviceDescriptor MatchAny(SlaveIdentity identity, IEnumerable<EsiDeviceLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(libraries);

        var libraryList = libraries.ToList();

        var vendorMatchingLibraries = libraryList
            .Where(l => l.Vendor.Id == identity.VendorId)
            .ToList();

        if (vendorMatchingLibraries.Count == 0)
        {
            throw new SlaveIdentityMismatchException(
                $"No ESI library matches the discovered slave's Vendor Id 0x{identity.VendorId:X8} " +
                $"({libraryList.Count} ESI librar{(libraryList.Count == 1 ? "y was" : "ies were")} searched). " +
                "Is the ESI file for this vendor present in the ESI folder?");
        }

        var matches = new List<(EsiDeviceLibrary Library, EsiDeviceDescriptor Device)>();

        foreach (var library in vendorMatchingLibraries)
        {
            try
            {
                matches.Add((library, Match(identity, library)));
            }
            catch (SlaveIdentityMismatchException)
            {
                // This vendor-matching library simply does not declare the Product Code +
                // Revision Number combination -- keep searching the other libraries.
            }
        }

        if (matches.Count == 0)
        {
            throw new SlaveIdentityMismatchException(
                "No device in any ESI library matches the discovered slave " +
                $"(VendorId=0x{identity.VendorId:X8}, ProductCode=0x{identity.ProductCode:X8}, Revision=0x{identity.RevisionNumber:X8}). " +
                $"{vendorMatchingLibraries.Count} ESI librar{(vendorMatchingLibraries.Count == 1 ? "y matched" : "ies matched")} the " +
                $"vendor but declared no such device (out of {libraryList.Count} librar{(libraryList.Count == 1 ? "y" : "ies")} searched in total).");
        }

        if (matches.Count > 1)
        {
            var conflicts = string.Join(
                ", ",
                matches.Select(m => $"{m.Device.Name} (Vendor '{m.Library.Vendor.Name}' 0x{m.Library.Vendor.Id:X8})"));

            throw new SlaveIdentityMismatchException(
                $"{matches.Count} devices across different ESI libraries share VendorId=0x{identity.VendorId:X8} " +
                $"ProductCode=0x{identity.ProductCode:X8} Revision=0x{identity.RevisionNumber:X8} ({conflicts}); " +
                "the ESI folder is ambiguous for this slave.");
        }

        return matches[0].Device;
    }
}
