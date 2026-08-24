namespace EtherCAT.NET.Engine.Discovery;

/// <summary>
/// Thrown by <see cref="IdentityMatcher.Match"/> when a slave's discovered SII identity (Vendor Id
/// / Product Code / Revision Number) cannot be resolved to exactly one device in a parsed ESI
/// library — either the vendor itself does not match, no device's Product Code + Revision Number
/// matches, or (in a malformed ESI file) more than one device shares that same Product Code +
/// Revision Number. Deliberately loud: every later step (FMMU/SM register configuration, PDO byte
/// offsets) is taken verbatim from whatever <see cref="Esi.EsiDeviceDescriptor"/> discovery
/// resolves to, so silently proceeding against a best guess (or a null) is a safety concern, not
/// just a bookkeeping one.
/// </summary>
public sealed class SlaveIdentityMismatchException : Exception
{
    /// <summary>Creates a <see cref="SlaveIdentityMismatchException"/> with a message describing exactly what did not match.</summary>
    public SlaveIdentityMismatchException(string message) : base(message)
    {
    }
}
