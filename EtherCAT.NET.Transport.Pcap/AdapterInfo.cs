using SharpPcap;
using SharpPcap.LibPcap;

namespace EtherCAT.NET.Transport.Pcap;

/// <summary>
/// Describes one network adapter that SharpPcap/Npcap can see, without exposing any SharpPcap type.
/// This is the only thing about "which NIC" that <c>EtherCAT.NET</c> (WPF) ever needs to know, so the
/// UI layer can populate an adapter picker while depending only on this project's public surface
/// (<see cref="AdapterInfo"/>, <see cref="PcapAdapters"/>, <see cref="PcapEthernetFrameTransport"/>)
/// and never on SharpPcap itself.
/// </summary>
/// <param name="Id">
/// The adapter's SharpPcap device name (on Windows/Npcap, an opaque string such as
/// <c>\Device\NPF_{GUID}</c>). Pass this -- or the whole <see cref="AdapterInfo"/> -- to
/// <see cref="PcapEthernetFrameTransport"/>'s constructor to open this same adapter.
/// </param>
/// <param name="Description">A human-readable description (typically the NIC's friendly/vendor name) suitable for display in an adapter picker.</param>
/// <param name="IsUp">
/// Whether the adapter reports itself as up (connected/enabled). Best-effort: an adapter whose
/// up/down status could not be determined defaults to <c>true</c> so it is shown rather than hidden.
/// </param>
public sealed record AdapterInfo(string Id, string Description, bool IsUp);

/// <summary>
/// Enumerates the network adapters available for <see cref="PcapEthernetFrameTransport"/>, confining
/// every direct use of <see cref="SharpPcap.CaptureDeviceList"/> to this one place. Per the
/// implementation plan, a machine without Npcap installed must not crash the application:
/// <see cref="GetAvailableAdapters()"/> and <see cref="GetAvailableAdapters(out string?)"/> both catch
/// everything <see cref="CaptureDeviceList.Instance"/> can throw (typically a
/// <see cref="DllNotFoundException"/>/<see cref="TypeInitializationException"/> when the Npcap driver
/// DLL is missing) and report an empty list instead of letting the exception propagate.
/// </summary>
public static class PcapAdapters
{
    /// <summary>
    /// Enumerates available adapters, or an empty list if Npcap is not installed or enumeration
    /// otherwise failed. Never throws. Use <see cref="GetAvailableAdapters(out string?)"/> instead if
    /// the caller needs to show the user *why* the list came back empty.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> GetAvailableAdapters() => GetAvailableAdapters(out _);

    /// <summary>
    /// Enumerates available adapters. On success, returns every adapter
    /// <see cref="CaptureDeviceList.Instance"/> reports and sets <paramref name="error"/> to
    /// <c>null</c>. On failure (most commonly: Npcap is not installed) returns an empty list and sets
    /// <paramref name="error"/> to a message that is safe to show directly in the UI. Never throws.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> GetAvailableAdapters(out string? error)
    {
        try
        {
            var devices = CaptureDeviceList.Instance;
            var result = new List<AdapterInfo>(devices.Count);

            foreach (var device in devices)
            {
                var description = string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description;
                result.Add(new AdapterInfo(device.Name, description, IsAdapterUp(device)));
            }

            error = null;
            return result;
        }
        catch (Exception ex)
        {
            error =
                $"Could not list network adapters ({ex.GetType().Name}: {ex.Message}). Is Npcap installed? " +
                "Download it from https://npcap.com/ and enable \"Install Npcap in WinPcap API-compatible Mode\", then restart this app.";
            return [];
        }
    }

    /// <summary>
    /// Best-effort "is this adapter up" check via the libpcap interface flags exposed by the concrete
    /// <see cref="LibPcapLiveDevice"/> SharpPcap returns from <see cref="CaptureDeviceList"/>. If the
    /// device is some other <see cref="ILiveDevice"/> implementation, or the flag can't be read for any
    /// reason, defaults to <c>true</c> so the adapter is still shown rather than silently hidden.
    /// </summary>
    private static bool IsAdapterUp(ILiveDevice device)
    {
        const uint PcapIfUp = 0x2; // PCAP_IF_UP, from pcap/pcap.h -- libpcap's own "adapter is up" flag.

        if (device is LibPcapLiveDevice liveDevice)
        {
            try
            {
                return (liveDevice.Flags & PcapIfUp) != 0;
            }
            catch
            {
                return true;
            }
        }

        return true;
    }
}
