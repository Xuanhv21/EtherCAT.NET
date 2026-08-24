using EtherCAT.NET.Engine.Transport;
using SharpPcap;

namespace EtherCAT.NET.Transport.Pcap;

/// <summary>
/// <see cref="IEthernetFrameTransport"/> implementation over a real NIC, via SharpPcap/Npcap. Opens
/// the adapter named by <see cref="AdapterInfo.Id"/> (or an equivalent raw SharpPcap device name) in
/// promiscuous mode, optionally installs a BPF capture filter for EtherCAT's EtherType (<c>0x88A4</c>)
/// so unrelated traffic never reaches <see cref="FrameReceived"/>, and starts capturing immediately --
/// construction is "resolve device + open + filter + subscribe + start", matching
/// <see cref="IEthernetFrameTransport"/>'s shape (there is no separate Start method).
/// <see cref="Send"/> injects a frame via SharpPcap's packet-injection API.
/// </summary>
/// <remarks>
/// Every SharpPcap-specific type (<see cref="ILiveDevice"/>, <see cref="CaptureDeviceList"/>, ...)
/// stays confined to this class and to <see cref="PcapAdapters"/> -- <c>EtherCAT.NET</c> (WPF)
/// constructs this transport from a plain <see cref="AdapterInfo"/>/<see langword="string"/> and never
/// references SharpPcap itself.
/// </remarks>
public sealed class PcapEthernetFrameTransport : IEthernetFrameTransport
{
    /// <summary>
    /// BPF capture filter matching only Ethernet II frames carrying the EtherCAT EtherType
    /// (<c>0x88A4</c>) -- applied by default so that unrelated broadcast/multicast traffic on a NIC
    /// that is not perfectly isolated from the rest of the network never reaches
    /// <see cref="FrameReceived"/>.
    /// </summary>
    public const string EtherCatCaptureFilter = "ether proto 0x88a4";

    private readonly ILiveDevice _device;
    private bool _disposed;

    /// <summary>
    /// Opens the adapter described by <paramref name="adapter"/> (as returned by
    /// <see cref="PcapAdapters.GetAvailableAdapters()"/>) and immediately starts capturing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> is <c>null</c>.</exception>
    /// <exception cref="PcapTransportException">See the <see cref="string"/>-overload's exceptions.</exception>
    public PcapEthernetFrameTransport(AdapterInfo adapter, bool applyEtherCatCaptureFilter = true, int readTimeoutMilliseconds = 1)
        : this((adapter ?? throw new ArgumentNullException(nameof(adapter))).Id, applyEtherCatCaptureFilter, readTimeoutMilliseconds)
    {
    }

    /// <summary>
    /// Resolves <paramref name="adapterId"/> against <see cref="CaptureDeviceList.Instance"/>, opens it
    /// in promiscuous mode, and immediately starts capturing.
    /// </summary>
    /// <param name="adapterId">The SharpPcap device name of the adapter to open (an <see cref="AdapterInfo.Id"/>).</param>
    /// <param name="applyEtherCatCaptureFilter">
    /// When <c>true</c> (the default), installs <see cref="EtherCatCaptureFilter"/> so that only
    /// EtherCAT traffic is delivered to <see cref="FrameReceived"/>. Pass <c>false</c> to receive every
    /// frame the NIC sees (e.g. for diagnostics).
    /// </param>
    /// <param name="readTimeoutMilliseconds">
    /// The underlying libpcap read timeout, in milliseconds. Kept short by default (1 ms) so captured
    /// frames reach <see cref="FrameReceived"/> with minimal added latency -- appropriate for the
    /// engine's 10 ms cyclic exchange.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="adapterId"/> is null/empty/whitespace.</exception>
    /// <exception cref="PcapTransportException">
    /// Npcap is not installed (or <see cref="CaptureDeviceList.Instance"/> otherwise failed), no
    /// adapter named <paramref name="adapterId"/> was found, or the adapter could not be opened /
    /// filtered / put into capture (e.g. it is not capture-capable, or another process holds it
    /// exclusively).
    /// </exception>
    public PcapEthernetFrameTransport(string adapterId, bool applyEtherCatCaptureFilter = true, int readTimeoutMilliseconds = 1)
    {
        var device = ResolveDevice(adapterId);
        _device = device;
        Name = string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description;

        try
        {
            device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.Promiscuous,
                ReadTimeout = readTimeoutMilliseconds,
            });
        }
        catch (Exception ex)
        {
            throw new PcapTransportException(
                $"Failed to open capture device '{device.Name}' ({device.Description}) for EtherCAT traffic. " +
                "Is Npcap installed, and does this process have permission to open it?", ex);
        }

        if (applyEtherCatCaptureFilter)
        {
            try
            {
                device.Filter = EtherCatCaptureFilter;
            }
            catch (Exception ex)
            {
                device.Close();
                throw new PcapTransportException(
                    $"Opened capture device '{Name}' but failed to install the EtherCAT capture filter ('{EtherCatCaptureFilter}').", ex);
            }
        }

        device.OnPacketArrival += OnPacketArrival;

        try
        {
            device.StartCapture();
        }
        catch (Exception ex)
        {
            device.OnPacketArrival -= OnPacketArrival;
            device.Close();
            throw new PcapTransportException($"Opened capture device '{Name}' but failed to start capturing.", ex);
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>
    /// Injects <paramref name="frame"/> onto the wire via the underlying SharpPcap device.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This transport has already been disposed.</exception>
    /// <exception cref="PcapTransportException">The device rejected the frame (e.g. it went away, or the driver refused it).</exception>
    public void Send(ReadOnlyMemory<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bytes = frame.ToArray();
        try
        {
            _device.SendPacket(bytes, bytes.Length);
        }
        catch (Exception ex)
        {
            throw new PcapTransportException($"Failed to send a {bytes.Length}-byte frame via '{Name}'.", ex);
        }
    }

    /// <summary>
    /// SharpPcap's packet-arrival callback. Runs on SharpPcap's own capture thread, not the caller's.
    /// <see cref="PacketCapture.GetPacket"/> materializes the captured bytes into a
    /// <see cref="RawCapture"/> (a plain, independently-owned <c>byte[]</c>), which is what gets
    /// republished through <see cref="FrameReceived"/> -- so subscribers may hold on to it after this
    /// callback returns.
    /// </summary>
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        FrameReceived?.Invoke(this, raw.Data);
    }

    /// <summary>
    /// Stops capturing, unsubscribes, and closes the underlying device. Best-effort: any failure while
    /// stopping/closing is swallowed, since <see cref="Dispose"/> must not throw.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.OnPacketArrival -= OnPacketArrival;

        try
        {
            if (_device.Started)
            {
                _device.StopCapture();
            }
        }
        catch
        {
            // Best-effort: Dispose must not throw.
        }

        try
        {
            _device.Close();
        }
        catch
        {
            // Best-effort: Dispose must not throw.
        }
    }

    /// <summary>
    /// Finds the <see cref="ILiveDevice"/> named <paramref name="adapterId"/> in
    /// <see cref="CaptureDeviceList.Instance"/>, wrapping every failure mode (Npcap missing, adapter
    /// gone) in a <see cref="PcapTransportException"/> rather than letting a raw SharpPcap/libpcap
    /// exception escape.
    /// </summary>
    private static ILiveDevice ResolveDevice(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        CaptureDeviceList devices;
        try
        {
            devices = CaptureDeviceList.Instance;
        }
        catch (Exception ex)
        {
            throw new PcapTransportException(
                $"Could not enumerate capture adapters while looking for '{adapterId}'. Is Npcap installed?", ex);
        }

        foreach (var device in devices)
        {
            if (device.Name == adapterId)
            {
                return device;
            }
        }

        throw new PcapTransportException(
            $"No capture adapter named '{adapterId}' was found. It may have been disconnected, or Npcap may need reinstalling.");
    }
}

/// <summary>
/// Thrown by <see cref="PcapEthernetFrameTransport"/> when a SharpPcap/Npcap operation fails --
/// resolving or opening a device, applying the capture filter, starting capture, or sending a frame.
/// Wraps whatever underlying SharpPcap/libpcap exception occurred (if any); the plan requires that a
/// missing Npcap installation never crash the app, so every call site that can hit this catches it
/// (or, for adapter enumeration, <see cref="PcapAdapters"/> avoids throwing at all) and reports it to
/// the user instead.
/// </summary>
public sealed class PcapTransportException : Exception
{
    /// <summary>Creates a <see cref="PcapTransportException"/> with no inner exception.</summary>
    public PcapTransportException(string message) : base(message)
    {
    }

    /// <summary>Creates a <see cref="PcapTransportException"/> wrapping the SharpPcap/libpcap exception that caused it.</summary>
    public PcapTransportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
