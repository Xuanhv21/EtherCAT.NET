using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Mvvm;
using EtherCAT.NET.Transport.Pcap;
using Microsoft.Win32;

namespace EtherCAT.NET;

/// <summary>
/// The Milestone 1 bring-up view model described in the implementation plan's "UI WPF" section.
/// Owns the whole master pipeline for one adapter/one GROUP of slaves: opens
/// <see cref="PcapEthernetFrameTransport"/> for the selected <see cref="AdapterInfo"/>, runs
/// <see cref="SlaveDiscovery.DiscoverAllSlaves"/> against every ESI file currently loaded from
/// <see cref="EsiFolderPath"/> (any vendor's files side by side -- see <see cref="EsiCatalog"/> and
/// <see cref="RescanEsiFolder"/>), computes the combined process-image plan with
/// <see cref="ProcessImageBuilder.BuildMulti"/>, drives <see cref="MultiSlaveAlStateMachine"/>
/// INIT-&gt;PREOP-&gt;SAFEOP-&gt;OP for the whole group at once (wiring its <c>onSafeOpRequested</c>
/// hook to start <see cref="MultiSlaveCyclicExchangeService"/> at exactly the right instant, per
/// <see cref="MultiSlaveAlStateMachine"/>'s own remarks), and thereafter exposes the currently
/// <see cref="SelectedSlave"/>'s Statusword bits, the group's AL state, and the shared log as
/// bindable properties. <see cref="Slaves"/> lists every slave discovered, and the five DS402
/// Controlword buttons always target whichever one is currently selected -- never the whole group at
/// once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading:</b> this type is constructed on the UI thread, where it captures
/// <see cref="SynchronizationContext.Current"/>. Every property this class mutates from
/// <see cref="MultiSlaveCyclicExchangeService.StatusUpdated"/>/<see cref="MultiSlaveCyclicExchangeService.LogEmitted"/>
/// (which fire on the cyclic background thread) or from the Start/Stop bring-up/tear-down work
/// (which runs on a <see cref="Task.Run(Action)"/> thread pool thread so it never blocks the UI) is
/// only ever written back on the UI thread, via <see cref="SynchronizationContext.Post"/> — never
/// touched directly from a background thread.
/// </para>
/// <para>
/// <b>Start/Stop race gating:</b> a single <see cref="Interlocked"/>-guarded
/// <see cref="_lifecycleState"/> field (<see cref="LifecycleIdle"/>/<see cref="LifecycleStarting"/>/
/// <see cref="LifecycleRunning"/>/<see cref="LifecycleStopping"/>) ensures a second Start (or Stop)
/// click while one is already in flight is a silent no-op rather than a second concurrent bring-up
/// (or tear-down) racing the first.
/// </para>
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject
{
    // Locally-administered MAC address (U/L bit set, per IEEE 802) stamped on every outgoing frame.
    // Per EscClient's own remarks this may be "an arbitrary locally-administered address" -- nothing
    // in Milestone 1's single-NIC/point-to-point EtherCAT link depends on it matching the physical
    // adapter's real MAC.
    private static readonly MacAddress SourceMac = new(new byte[] { 0x02, 0xEC, 0xA7, 0x00, 0x00, 0x01 });

    private const int LifecycleIdle = 0;
    private const int LifecycleStarting = 1;
    private const int LifecycleRunning = 2;
    private const int LifecycleStopping = 3;

    private const int MaxLogEntries = 2000;

    // Deployment default for a brand-new machine's ESI folder -- an app-layer concern (where to look
    // on disk), not something EtherCAT.NET.Engine's EsiCatalog itself should know or hard-code.
    private const string DefaultEsiFolderPath = @"D:\EtherCAT\ESI";

    private readonly SynchronizationContext? _uiContext;
    private int _lifecycleState = LifecycleIdle;

    private PcapEthernetFrameTransport? _transport;
    private MultiSlaveCyclicExchangeService? _cyclicExchange;
    private EsiCatalog _esiCatalog = new([]);

    private AdapterInfo? _selectedAdapter;
    private SlaveListItem? _selectedSlave;
    private string? _adapterError;
    private bool _isBusy;
    private bool _isRunning;
    private string _alStateText = "Stopped";

    private bool _readyToSwitchOn;
    private bool _switchedOn;
    private bool _operationEnabled;
    private bool _fault;
    private bool _voltageEnabled;
    private bool _quickStop;
    private bool _switchOnDisabled;
    private bool _warning;
    private bool _isDataFresh;

    private string _esiFolderPath = DefaultEsiFolderPath;
    private string _esiCatalogSummary = "Not scanned yet.";

    private double _jogVelocity = 50.0;
    private double _jogAcceleration = 200.0;
    private double _jogDeceleration = 400.0;
    private int _jogSlaveIndex = -1;
    private DispatcherTimer? _jogHeartbeatTimer;

    private volatile MultiSlaveProcessImageSnapshot? _lastSnapshot;
    private bool _isServoSequenceRunning;

    /// <summary>Creates the view model. Must be constructed on the UI thread — its constructor captures <see cref="SynchronizationContext.Current"/> for later marshaling.</summary>
    public MainWindowViewModel()
    {
        _uiContext = SynchronizationContext.Current;

        Adapters = [];
        Slaves = [];
        LogEntries = [];

        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => CanStop);
        RefreshAdaptersCommand = new RelayCommand(LoadAdapters, () => !IsBusy && !IsRunning);
        BrowseEsiFolderCommand = new RelayCommand(BrowseEsiFolder, () => !IsBusy && !IsRunning);
        RescanEsiFolderCommand = new RelayCommand(RescanEsiFolder, () => !IsBusy && !IsRunning);

        ServoOnCommand = new RelayCommand(() => _ = ServoOnAsync(), () => CanServoOn);
        ServoOffCommand = new RelayCommand(() => SendControlword("Servo OFF", Ds402Controlword.DisableVoltage), () => CanSendControlword);

        ShutdownCommand = new RelayCommand(() => SendControlword("Shutdown", Ds402Controlword.Shutdown), () => CanSendControlword);
        SwitchOnCommand = new RelayCommand(() => SendControlword("Switch On", Ds402Controlword.SwitchOn), () => CanSendControlword);
        EnableOperationCommand = new RelayCommand(() => SendControlword("Enable Operation", Ds402Controlword.EnableOperation), () => CanSendControlword);
        DisableVoltageCommand = new RelayCommand(() => SendControlword("Disable Voltage", Ds402Controlword.DisableVoltage), () => CanSendControlword);
        FaultResetCommand = new RelayCommand(() => SendControlword("Fault Reset", Ds402Controlword.FaultReset), () => CanSendControlword);

        LoadAdapters();

        // First-run convenience: a brand-new machine's ESI folder starts out empty, so seed it with
        // the embedded Panasonic file before the initial scan -- see SeedEsiFolderIfEmpty's remarks.
        SeedEsiFolderIfEmpty();
        RescanEsiFolder();
    }

    /// <summary>Every adapter <see cref="PcapAdapters.GetAvailableAdapters()"/> reported the last time it was called — never throws, never null, empty when Npcap is missing.</summary>
    public ObservableCollection<AdapterInfo> Adapters { get; }

    /// <summary>
    /// Every slave the last successful <see cref="RunBringUp"/> discovered, in discovery order
    /// (matching the index every multi-slave engine type addresses that slave by). Empty until a
    /// bring-up has actually completed discovery at least once; cleared on <see cref="StopAsync"/>.
    /// </summary>
    public ObservableCollection<SlaveListItem> Slaves { get; }

    /// <summary>Scrolling bring-up/status log, newest entries at the end.</summary>
    public ObservableCollection<LogEntry> LogEntries { get; }

    /// <summary>The adapter currently chosen in the picker.</summary>
    public AdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetField(ref _selectedAdapter, value))
            {
                RaiseCanExecuteChangedAll();
            }
        }
    }

    /// <summary>
    /// Non-null when the last adapter enumeration failed (typically: Npcap is not installed) — safe
    /// to show directly in the UI in place of the adapter picker. Null when enumeration succeeded,
    /// even if it found zero adapters.
    /// </summary>
    public string? AdapterError
    {
        get => _adapterError;
        private set => SetField(ref _adapterError, value);
    }

    /// <summary>
    /// The slave currently selected in the picker: the Statusword panel and the five DS402
    /// Controlword buttons always act on exactly this one slave, never the whole group at once.
    /// Settable at any time (selecting does not touch the running engine, only which slave's live
    /// data is displayed/controlled) but only meaningful once <see cref="Slaves"/> is non-empty.
    /// </summary>
    public SlaveListItem? SelectedSlave
    {
        get => _selectedSlave;
        set
        {
            if (SetField(ref _selectedSlave, value))
            {
                RaiseCanExecuteChangedAll();
                OnPropertyChanged(nameof(CanJog));
            }
        }
    }

    /// <summary><c>true</c> while a Start or Stop bring-up/tear-down is actually in flight on the background thread.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCanExecuteChangedAll();
                OnPropertyChanged(nameof(CanEditAdapterSelection));
            }
        }
    }

    /// <summary><c>true</c> once the bring-up sequence has reached OP and the cyclic exchange is live; drives whether the five DS402 buttons and Stop are enabled.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                RaiseCanExecuteChangedAll();
                OnPropertyChanged(nameof(CanEditAdapterSelection));
                OnPropertyChanged(nameof(CanJog));
            }
        }
    }

    /// <summary><c>true</c> when the adapter ComboBox/Refresh button should be enabled — i.e. not busy and not already running.</summary>
    public bool CanEditAdapterSelection => !IsBusy && !IsRunning;

    /// <summary>Human-readable current AL state (e.g. "Init", "PreOp", "SafeOp", "Op"), annotated with "(FAULTED)" once the cyclic exchange has stopped itself after too many consecutive failed cycles.</summary>
    public string AlStateText
    {
        get => _alStateText;
        private set => SetField(ref _alStateText, value);
    }

    /// <summary>CiA 402 Statusword (0x6041) bit 0 — Ready to switch on.</summary>
    public bool ReadyToSwitchOn { get => _readyToSwitchOn; private set => SetField(ref _readyToSwitchOn, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 1 — Switched on.</summary>
    public bool SwitchedOn { get => _switchedOn; private set => SetField(ref _switchedOn, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 2 — Operation enabled.</summary>
    public bool OperationEnabled { get => _operationEnabled; private set => SetField(ref _operationEnabled, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 3 — Fault.</summary>
    public bool Fault { get => _fault; private set => SetField(ref _fault, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 4 — Voltage enabled.</summary>
    public bool VoltageEnabled { get => _voltageEnabled; private set => SetField(ref _voltageEnabled, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 5 — Quick stop.</summary>
    public bool QuickStop { get => _quickStop; private set => SetField(ref _quickStop, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 6 — Switch on disabled.</summary>
    public bool SwitchOnDisabled { get => _switchOnDisabled; private set => SetField(ref _switchOnDisabled, value); }

    /// <summary>CiA 402 Statusword (0x6041) bit 7 — Warning.</summary>
    public bool Warning { get => _warning; private set => SetField(ref _warning, value); }

    /// <summary><c>true</c> when the most recent cycle's LRW exchange succeeded (WKC as expected) — the Statusword bits above reflect that cycle; when <c>false</c> they are held over from the last cycle that did succeed.</summary>
    public bool IsDataFresh { get => _isDataFresh; private set => SetField(ref _isDataFresh, value); }

    /// <summary>
    /// The jog speed the selected slave ramps toward while a jog button is held, in raw position
    /// units/sec — see <see cref="MultiSlaveCyclicExchangeService.JogVelocity"/>. No unit scaling
    /// exists anywhere in this milestone (raw encoder counts, not mm/degrees/RPM): start small and
    /// increase gradually while watching the real motor. Pushed to the live engine immediately if a
    /// bring-up is already running.
    /// </summary>
    public double JogVelocity
    {
        get => _jogVelocity;
        set
        {
            if (SetField(ref _jogVelocity, value) && _cyclicExchange is not null)
            {
                _cyclicExchange.JogVelocity = value;
            }
        }
    }

    /// <summary>How quickly jog speed ramps UP toward <see cref="JogVelocity"/> (raw units/sec²) — see <see cref="MultiSlaveCyclicExchangeService.JogAcceleration"/>. Pushed to the live engine immediately if a bring-up is already running.</summary>
    public double JogAcceleration
    {
        get => _jogAcceleration;
        set
        {
            if (SetField(ref _jogAcceleration, value) && _cyclicExchange is not null)
            {
                _cyclicExchange.JogAcceleration = value;
            }
        }
    }

    /// <summary>How quickly jog speed ramps DOWN to 0 on release (raw units/sec²) — see <see cref="MultiSlaveCyclicExchangeService.JogDeceleration"/>. Pushed to the live engine immediately if a bring-up is already running.</summary>
    public double JogDeceleration
    {
        get => _jogDeceleration;
        set
        {
            if (SetField(ref _jogDeceleration, value) && _cyclicExchange is not null)
            {
                _cyclicExchange.JogDeceleration = value;
            }
        }
    }

    /// <summary><c>true</c> when the two Jog buttons (and the velocity/acceleration/deceleration boxes) should be enabled — running, with a slave actually selected. Shares the exact same gate as the five DS402 Controlword buttons.</summary>
    public bool CanJog => CanSendControlword;

    /// <summary><c>true</c> while <see cref="ServoOnAsync"/> is actively walking the DS402 sequence for some slave (disables re-entrant Servo ON clicks; the raw DS402 buttons and Servo OFF remain available regardless).</summary>
    public bool IsServoSequenceRunning
    {
        get => _isServoSequenceRunning;
        private set
        {
            if (SetField(ref _isServoSequenceRunning, value))
            {
                RaiseCanExecuteChangedAll();
            }
        }
    }

    private bool CanServoOn => CanSendControlword && !IsServoSequenceRunning;

    /// <summary>
    /// Folder scanned for ESI XML files (any vendor's files side by side — see
    /// <see cref="EsiCatalog.LoadFolder"/>). Defaults to <see cref="DefaultEsiFolderPath"/>; changed
    /// only via <see cref="BrowseEsiFolderCommand"/>.
    /// </summary>
    public string EsiFolderPath
    {
        get => _esiFolderPath;
        private set => SetField(ref _esiFolderPath, value);
    }

    /// <summary>
    /// Short human-readable result of the last <see cref="RescanEsiFolder"/> (e.g. "Loaded 3 device(s)
    /// from 2 file(s) in 'D:\EtherCAT\ESI'."). Per-file parse failures are not repeated here — each is
    /// instead appended to <see cref="LogEntries"/> as its own "ESI parse failed: ..." line.
    /// </summary>
    public string EsiCatalogSummary
    {
        get => _esiCatalogSummary;
        private set => SetField(ref _esiCatalogSummary, value);
    }

    /// <summary>Re-enumerates <see cref="Adapters"/>. Never throws, per <see cref="PcapAdapters.GetAvailableAdapters(out string?)"/>.</summary>
    public RelayCommand RefreshAdaptersCommand { get; }

    /// <summary>Opens a Win32 folder picker for <see cref="EsiFolderPath"/> and, if the user confirms a folder, rescans it.</summary>
    public RelayCommand BrowseEsiFolderCommand { get; }

    /// <summary>Re-runs <see cref="RescanEsiFolder"/> against the current <see cref="EsiFolderPath"/>.</summary>
    public RelayCommand RescanEsiFolderCommand { get; }

    /// <summary>Opens the selected adapter and runs the full discovery -&gt; PREOP -&gt; SAFEOP -&gt; OP bring-up.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Stops the cyclic exchange (sending one final Disable Voltage cycle first, per <see cref="CyclicExchangeService.Stop"/>) and closes the transport.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Walks the standard DS402 Shutdown -&gt; Switch On -&gt; Enable Operation sequence for the selected slave, waiting for each step's Statusword confirmation before sending the next — see <see cref="ServoOnAsync"/>.</summary>
    public RelayCommand ServoOnCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.DisableVoltage"/> (0x0000) to the selected slave — immediate, no waiting (the drive's power stage drops out right away).</summary>
    public RelayCommand ServoOffCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.Shutdown"/> (0x0006).</summary>
    public RelayCommand ShutdownCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.SwitchOn"/> (0x0007).</summary>
    public RelayCommand SwitchOnCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.EnableOperation"/> (0x000F).</summary>
    public RelayCommand EnableOperationCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.DisableVoltage"/> (0x0000).</summary>
    public RelayCommand DisableVoltageCommand { get; }

    /// <summary>Sends Controlword = <see cref="Ds402Controlword.FaultReset"/> (0x0080).</summary>
    public RelayCommand FaultResetCommand { get; }

    private bool CanStart => !IsBusy && !IsRunning && SelectedAdapter is not null && _esiCatalog.Libraries.Count > 0;

    private bool CanStop => !IsBusy && IsRunning;

    /// <summary>The five DS402 Controlword buttons all share this gate: the group must be running, AND a slave must actually be selected to receive the command.</summary>
    private bool CanSendControlword => IsRunning && SelectedSlave is not null;

    /// <summary>
    /// Enumerates available capture adapters via <see cref="PcapAdapters.GetAvailableAdapters(out string?)"/>,
    /// which never throws — the constructor's call to this method is the "cannot crash if Npcap is
    /// missing" path the implementation plan requires. Wrapped in an extra try/catch anyway as
    /// defense in depth.
    /// </summary>
    private void LoadAdapters()
    {
        try
        {
            var adapters = PcapAdapters.GetAvailableAdapters(out var error);

            Adapters.Clear();
            foreach (var adapter in adapters)
            {
                Adapters.Add(adapter);
            }

            AdapterError = error;
            SelectedAdapter = Adapters.Count > 0 ? Adapters[0] : null;
        }
        catch (Exception ex)
        {
            Adapters.Clear();
            AdapterError = $"Could not list network adapters ({ex.GetType().Name}: {ex.Message}).";
            SelectedAdapter = null;
        }
    }

    /// <summary>
    /// Ensures a brand-new machine's <see cref="EsiFolderPath"/> is not left empty by seeding it with
    /// the embedded Panasonic MINAS A6BE ESI file the very first time, via
    /// <see cref="EsiCatalog.SeedIfEmpty"/>. The embedded resource is located exactly the way
    /// <see cref="EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be"/> locates it -- by
    /// <see cref="Assembly.GetManifestResourceNames"/> matching
    /// <see cref="EsiXmlParser.PanasonicMinasA6BeResourceFileName"/> -- because
    /// <see cref="EsiCatalog.SeedIfEmpty"/> needs the raw resource <see cref="Stream"/>, not an
    /// already-parsed library. A no-op once the folder already holds any <c>*.xml</c> file (see
    /// <see cref="EsiCatalog.SeedIfEmpty"/>'s own remarks). Never throws -- any failure (e.g. the
    /// folder is unwritable) is logged via <see cref="AppendLog"/> instead, the same defensive spirit
    /// as <see cref="LoadAdapters"/>.
    /// </summary>
    private void SeedEsiFolderIfEmpty()
    {
        try
        {
            var assembly = typeof(EsiXmlParser).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(EsiXmlParser.PanasonicMinasA6BeResourceFileName, StringComparison.Ordinal));

            if (resourceName is null)
            {
                AppendLog($"Could not seed ESI folder: no embedded resource ending in '{EsiXmlParser.PanasonicMinasA6BeResourceFileName}' was found.");
                return;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                AppendLog($"Could not seed ESI folder: embedded resource '{resourceName}' could not be opened.");
                return;
            }

            EsiCatalog.SeedIfEmpty(EsiFolderPath, EsiXmlParser.PanasonicMinasA6BeResourceFileName, stream);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not seed ESI folder '{EsiFolderPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a Win32 folder picker (<see cref="OpenFolderDialog"/>) pre-set to the current
    /// <see cref="EsiFolderPath"/> and, if the user confirms a folder, switches <see cref="EsiFolderPath"/>
    /// to it and re-runs <see cref="RescanEsiFolder"/>. Only invoked via <see cref="BrowseEsiFolderCommand"/>,
    /// which is gated the same as <see cref="RefreshAdaptersCommand"/> so the folder cannot change out
    /// from under a live bring-up.
    /// </summary>
    private void BrowseEsiFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select ESI Folder",
        };

        if (Directory.Exists(EsiFolderPath))
        {
            dialog.InitialDirectory = EsiFolderPath;
            dialog.FolderName = EsiFolderPath;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        EsiFolderPath = dialog.FolderName;
        RescanEsiFolder();
    }

    /// <summary>
    /// Re-loads <see cref="EsiFolderPath"/> via <see cref="EsiCatalog.LoadFolder"/> and stores the
    /// result in <see cref="_esiCatalog"/> for <see cref="RunBringUp"/> to discover against. Updates
    /// <see cref="EsiCatalogSummary"/> with a short "Loaded N device(s) from M file(s)..." line and,
    /// per failed file, appends one "ESI parse failed: ..." line to <see cref="LogEntries"/> via
    /// <see cref="AppendLog"/> -- the same "surface loudly, never silently swallow" spirit already used
    /// elsewhere in this class. Never throws: a folder that does not exist yet, cannot be read, or any
    /// other failure from <see cref="EsiCatalog.LoadFolder"/> itself is logged instead, leaving
    /// whatever catalog was previously loaded (if any) in place rather than losing it to a failed
    /// rescan.
    /// </summary>
    private void RescanEsiFolder()
    {
        try
        {
            var catalog = EsiCatalog.LoadFolder(EsiFolderPath);
            _esiCatalog = catalog;

            var deviceCount = catalog.Libraries.Sum(l => l.Devices.Count);
            var failures = catalog.Entries.Where(e => e.Error is not null).ToList();

            var summary = $"Loaded {deviceCount} device(s) from {catalog.Libraries.Count} file(s) in '{EsiFolderPath}'.";
            if (failures.Count > 0)
            {
                summary += $" ({failures.Count} file(s) failed to parse -- see log.)";
            }

            EsiCatalogSummary = summary;

            foreach (var failure in failures)
            {
                AppendLog($"ESI parse failed: {Path.GetFileName(failure.FilePath)} - {failure.Error}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not scan ESI folder '{EsiFolderPath}': {ex.Message}");
        }
        finally
        {
            RaiseCanExecuteChangedAll();
        }
    }

    private async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _lifecycleState, LifecycleStarting, LifecycleIdle) != LifecycleIdle)
        {
            return;
        }

        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            Interlocked.Exchange(ref _lifecycleState, LifecycleIdle);
            return;
        }

        IsBusy = true;
        AppendLog($"Starting bring-up on adapter '{adapter.Description}'...");

        try
        {
            await Task.Run(() => RunBringUp(adapter)).ConfigureAwait(true);

            Interlocked.Exchange(ref _lifecycleState, LifecycleRunning);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            // RunBringUp's own catch already stopped any partially-started CyclicExchangeService
            // and disposed the transport (on the background thread, before this await rethrows
            // here) -- nothing left to clean up on this side.
            AppendLog($"Start failed: {ex.Message}");
            AlStateText = "Stopped";
            Interlocked.Exchange(ref _lifecycleState, LifecycleIdle);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _lifecycleState, LifecycleStopping, LifecycleRunning) != LifecycleRunning)
        {
            return;
        }

        EndJog(); // defensive: a jog button held through Stop must not keep renewing after the service is gone.

        IsBusy = true;
        AppendLog("Stopping...");

        var cyclic = _cyclicExchange;
        var transport = _transport;

        try
        {
            await Task.Run(() =>
            {
                // CyclicExchangeService.Stop sends one final Controlword=DisableVoltage exchange
                // itself before its thread exits -- see its own remarks. Nothing more to do here.
                cyclic?.Stop();
                transport?.Dispose();
            }).ConfigureAwait(true);

            AppendLog("Stopped.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error while stopping: {ex.Message}");
        }
        finally
        {
            if (cyclic is not null)
            {
                cyclic.StatusUpdated -= OnStatusUpdated;
                cyclic.LogEmitted -= AppendLog;
            }

            _cyclicExchange = null;
            _transport = null;
            AlStateText = "Stopped";
            IsRunning = false;
            IsBusy = false;
            RunOnUiThread(() =>
            {
                Slaves.Clear();
                SelectedSlave = null;
            });
            Interlocked.Exchange(ref _lifecycleState, LifecycleIdle);
        }
    }

    /// <summary>
    /// Runs on a thread pool thread (never the UI thread): opens the transport, discovers every
    /// slave on the bus, builds the combined process-image plan, and drives
    /// <see cref="MultiSlaveAlStateMachine"/> all the way to OP for the whole group at once, wiring
    /// its <c>onSafeOpRequested</c> hook to start <see cref="MultiSlaveCyclicExchangeService"/> at
    /// exactly the point its remarks describe. Populates <see cref="Slaves"/> (and selects the first
    /// one) as soon as discovery succeeds, before the state machine work even starts, so the picker
    /// is usable the moment it can be. On any failure, disposes whatever was opened and rethrows so
    /// <see cref="StartAsync"/> can report it.
    /// </summary>
    private void RunBringUp(AdapterInfo adapter)
    {
        var transport = new PcapEthernetFrameTransport(adapter);
        MultiSlaveCyclicExchangeService? cyclic = null;

        try
        {
            var escClient = new EscClient(transport, SourceMac);
            var catalog = _esiCatalog;

            if (catalog.Libraries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No usable ESI files are loaded from '{EsiFolderPath}'. Add at least one valid " +
                    "ESI XML file to that folder, click Rescan, then Start again.");
            }

            AppendLog("Discovering slave(s) on the bus...");
            var discoveries = SlaveDiscovery.DiscoverAllSlaves(escClient, catalog.Libraries);

            if (discoveries.Count == 0)
            {
                throw new InvalidOperationException("No slaves responded on the bus.");
            }

            var slaveItems = new List<SlaveListItem>(discoveries.Count);
            for (var i = 0; i < discoveries.Count; i++)
            {
                var d = discoveries[i];
                AppendLog($"  Slave {i}: matched '{d.Device.Name}' (ProductCode=0x{d.Device.ProductCode:X8}, " +
                          $"Revision=0x{d.Device.RevisionNumber:X8}), assigned Configured Station Address 0x{d.StationAddress:X4}.");
                slaveItems.Add(new SlaveListItem(i, d.StationAddress, d.Device.Name));
            }

            AppendLog($"Discovered {discoveries.Count} slave(s) total.");

            RunOnUiThread(() =>
            {
                Slaves.Clear();
                foreach (var item in slaveItems)
                {
                    Slaves.Add(item);
                }

                SelectedSlave = Slaves.Count > 0 ? Slaves[0] : null;
            });

            var devices = discoveries.Select(d => d.Device).ToList();
            var stationAddresses = discoveries.Select(d => d.StationAddress).ToList();
            var plan = ProcessImageBuilder.BuildMulti(discoveries.Select(d => (d.StationAddress, d.Device)).ToList());

            var stateMachine = new MultiSlaveAlStateMachine(escClient, transport, SourceMac, stationAddresses);
            cyclic = new MultiSlaveCyclicExchangeService(transport, SourceMac, plan)
            {
                JogVelocity = JogVelocity,
                JogAcceleration = JogAcceleration,
                JogDeceleration = JogDeceleration,
            };
            cyclic.StatusUpdated += OnStatusUpdated;
            cyclic.LogEmitted += AppendLog;

            AppendLog("Requesting PREOP for all slaves...");
            stateMachine.TransitionToPreOp(devices);
            cyclic.SetAlState(AlState.PreOp);
            AppendLog("Reached PREOP.");

            AppendLog("Requesting SAFEOP for all slaves and starting cyclic exchange...");
            stateMachine.TransitionToSafeOp(devices, plan, onSafeOpRequested: cyclic.Start);
            cyclic.SetAlState(AlState.SafeOp);
            AppendLog("Reached SAFEOP.");

            AppendLog("Waiting for consecutive good cyclic exchanges before requesting OP...");
            stateMachine.TransitionToOp(plan);
            cyclic.SetAlState(AlState.Op);
            AppendLog("Reached OP.");

            _transport = transport;
            _cyclicExchange = cyclic;
        }
        catch
        {
            // cyclic.Start() may already have been called (inside TransitionToSafeOp's
            // onSafeOpRequested hook, per MultiSlaveAlStateMachine's own remarks) even though a
            // *later* step (e.g. TransitionToOp's readiness wait) is what actually failed -- so an
            // already-live cyclic thread must be stopped cleanly (its own final safe-shutdown
            // exchange still going out over the still-open transport) before the transport is
            // disposed, rather than left running to fail on its own against a disposed transport.
            if (cyclic is not null)
            {
                cyclic.StatusUpdated -= OnStatusUpdated;
                cyclic.LogEmitted -= AppendLog;

                try
                {
                    cyclic.Stop(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Best-effort: this bring-up attempt is already failing; a further failure
                    // while stopping the cyclic exchange doesn't change the outcome.
                }
            }

            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <see cref="MultiSlaveCyclicExchangeService.StatusUpdated"/> handler — runs on the cyclic
    /// background thread. Publishes the group-level AL state/data-freshness immediately, and the
    /// currently <see cref="SelectedSlave"/>'s Statusword bits specifically (every other slave's data
    /// is still in <paramref name="snapshot"/>, it is simply not the one bound to the UI right now).
    /// Marshals every bound-property write onto the UI thread via <see cref="RunOnUiThread"/>.
    /// </summary>
    private void OnStatusUpdated(MultiSlaveProcessImageSnapshot snapshot)
    {
        // Cached immediately, on the cyclic thread, independent of which slave the UI happens to
        // have selected right now -- ServoOnAsync polls this (for whichever slave IT is sequencing)
        // rather than depending on the UI-bound Statusword properties below, which only ever reflect
        // SelectedSlave.
        _lastSnapshot = snapshot;

        RunOnUiThread(() =>
        {
            AlStateText = snapshot.IsFaulted ? $"{snapshot.AlState} (FAULTED)" : snapshot.AlState.ToString();
            IsDataFresh = snapshot.IsDataFresh;

            var index = SelectedSlave?.Index ?? -1;
            if (index < 0 || index >= snapshot.Slaves.Count)
            {
                return;
            }

            var status = snapshot.Slaves[index].Status;
            ReadyToSwitchOn = status.ReadyToSwitchOn;
            SwitchedOn = status.SwitchedOn;
            OperationEnabled = status.OperationEnabled;
            Fault = status.Fault;
            VoltageEnabled = status.VoltageEnabled;
            QuickStop = status.QuickStop;
            SwitchOnDisabled = status.SwitchOnDisabled;
            Warning = status.Warning;
        });
    }

    /// <summary>Sends Controlword <paramref name="value"/> to whichever slave is currently <see cref="SelectedSlave"/> -- never the whole group. A no-op (aside from logging) if none is selected; <see cref="CanSendControlword"/> already gates the five buttons on that.</summary>
    private void SendControlword(string name, ushort value)
    {
        var slave = SelectedSlave;
        if (slave is null)
        {
            return;
        }

        _cyclicExchange?.SetControlword(slave.Index, value);
        AppendLog($"Slave {slave.Index} (station 0x{slave.StationAddress:X4}): Controlword -> {name} (0x{value:X4}).");
    }

    /// <summary>
    /// Convenience "Servo ON" sequence for whichever slave is currently <see cref="SelectedSlave"/>:
    /// walks the standard DS402 power-state sequence — Shutdown, then Switch On, then Enable
    /// Operation — waiting after each command for that slave's own Statusword (via
    /// <see cref="RunServoOnStepAsync"/>, polling <see cref="_lastSnapshot"/>) to actually confirm
    /// the corresponding state before sending the next one, rather than firing all three blindly.
    /// Which slave is sequenced is fixed to whatever was selected when this started, exactly like
    /// <see cref="BeginJog"/>. Logs every step; if any step times out, logs the failure and stops
    /// without attempting the remaining steps. The five raw DS402 buttons stay available for manual,
    /// step-by-step control — this is purely a convenience wrapper around the exact same
    /// <see cref="MultiSlaveCyclicExchangeService.SetControlword"/> call they use, not a new command
    /// path. Guarded by <see cref="IsServoSequenceRunning"/> against re-entrant clicks.
    /// </summary>
    private async Task ServoOnAsync()
    {
        var slave = SelectedSlave;
        if (slave is null || _cyclicExchange is null || !IsRunning || IsServoSequenceRunning)
        {
            return;
        }

        IsServoSequenceRunning = true;
        var index = slave.Index;

        try
        {
            AppendLog($"Slave {index} (station 0x{slave.StationAddress:X4}): Servo ON sequence starting...");

            if (!await RunServoOnStepAsync(index, Ds402Controlword.Shutdown, "Shutdown", s => s.ReadyToSwitchOn, "Ready to switch on").ConfigureAwait(true))
            {
                return;
            }

            if (!await RunServoOnStepAsync(index, Ds402Controlword.SwitchOn, "Switch On", s => s.SwitchedOn, "Switched on").ConfigureAwait(true))
            {
                return;
            }

            if (!await RunServoOnStepAsync(index, Ds402Controlword.EnableOperation, "Enable Operation", s => s.OperationEnabled, "Operation enabled").ConfigureAwait(true))
            {
                return;
            }

            AppendLog($"Slave {index}: Servo ON complete (Operation enabled).");
        }
        finally
        {
            IsServoSequenceRunning = false;
        }
    }

    /// <summary>
    /// Sends <paramref name="controlword"/> to slave <paramref name="slaveIndex"/>, then polls
    /// <see cref="_lastSnapshot"/> (updated every cycle by <see cref="OnStatusUpdated"/>, independent
    /// of <see cref="SelectedSlave"/>) roughly every 20 ms until <paramref name="reached"/> is true
    /// for that slave's Statusword, or <paramref name="timeout"/> (default 3 seconds) elapses.
    /// </summary>
    /// <returns><c>true</c> if <paramref name="reached"/> became true in time; <c>false</c> (after logging why) on timeout.</returns>
    private async Task<bool> RunServoOnStepAsync(int slaveIndex, ushort controlword, string commandName, Func<Ds402Statusword, bool> reached, string stateName, TimeSpan? timeout = null)
    {
        _cyclicExchange?.SetControlword(slaveIndex, controlword);
        AppendLog($"Slave {slaveIndex}: Controlword -> {commandName} (0x{controlword:X4}); waiting for '{stateName}'...");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _lastSnapshot;
            if (snapshot is not null && slaveIndex < snapshot.Slaves.Count && reached(snapshot.Slaves[slaveIndex].Status))
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(true);
        }

        AppendLog($"Slave {slaveIndex}: Servo ON failed — timed out waiting for '{stateName}' after {commandName}.");
        return false;
    }

    /// <summary>
    /// Starts jogging the currently <see cref="SelectedSlave"/> in <paramref name="direction"/> (-1
    /// or +1): sends one immediate <see cref="MultiSlaveCyclicExchangeService.SetJog"/> call, then
    /// starts a UI-thread <see cref="DispatcherTimer"/> that renews it well inside the engine's own
    /// <see cref="MultiSlaveCyclicExchangeService.JogHeartbeatTimeout"/> for as long as the caller
    /// keeps jogging held (i.e. until <see cref="EndJog"/> is called). Called from
    /// <c>MainWindow.xaml.cs</c>'s jog-button mouse-down handler; a no-op if nothing is running or no
    /// slave is selected (mirrors <see cref="CanJog"/>, which already gates the buttons themselves).
    /// Which slave is jogged is fixed to whatever was selected at the moment this is called — it is
    /// not re-read from <see cref="SelectedSlave"/> again until the next <see cref="BeginJog"/>.
    /// </summary>
    public void BeginJog(int direction)
    {
        var slave = SelectedSlave;
        var cyclic = _cyclicExchange;
        if (slave is null || cyclic is null || !IsRunning)
        {
            return;
        }

        EndJog(); // stop any previous jog (e.g. a stray double-press) before starting a new one.

        _jogSlaveIndex = slave.Index;
        cyclic.SetJog(_jogSlaveIndex, direction);
        AppendLog($"Slave {_jogSlaveIndex} (station 0x{slave.StationAddress:X4}): jog {(direction > 0 ? "+" : "-")} started " +
                  $"(target {JogVelocity:F0} units/sec, accel {JogAcceleration:F0}, decel {JogDeceleration:F0}).");

        _jogHeartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _jogHeartbeatTimer.Tick += (_, _) => cyclic.SetJog(_jogSlaveIndex, direction);
        _jogHeartbeatTimer.Start();
    }

    /// <summary>
    /// Stops whatever jog <see cref="BeginJog"/> last started (a no-op if none is active): stops the
    /// heartbeat-renewal timer and sends one final <see cref="MultiSlaveCyclicExchangeService.SetJog"/>
    /// with direction 0. Called from the jog buttons' mouse-up/lost-capture handlers — and, defensively,
    /// from <see cref="StopAsync"/> in case a jog button is somehow still held through Stop.
    /// </summary>
    public void EndJog()
    {
        _jogHeartbeatTimer?.Stop();
        _jogHeartbeatTimer = null;

        if (_jogSlaveIndex < 0)
        {
            return;
        }

        var slaveIndex = _jogSlaveIndex;
        _jogSlaveIndex = -1;

        _cyclicExchange?.SetJog(slaveIndex, 0);
        AppendLog($"Slave {slaveIndex}: jog stopped.");
    }

    /// <summary>
    /// Appends one entry to <see cref="LogEntries"/>, marshaled onto the UI thread. Safe to call
    /// from any thread — used both as the <see cref="CyclicExchangeService.LogEmitted"/> handler and
    /// for this view model's own bring-up/shutdown narration.
    /// </summary>
    private void AppendLog(string message)
    {
        RunOnUiThread(() =>
        {
            LogEntries.Add(LogEntry.Now(message));

            while (LogEntries.Count > MaxLogEntries)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private void RaiseCanExecuteChangedAll()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshAdaptersCommand.RaiseCanExecuteChanged();
        BrowseEsiFolderCommand.RaiseCanExecuteChanged();
        RescanEsiFolderCommand.RaiseCanExecuteChanged();
        ServoOnCommand.RaiseCanExecuteChanged();
        ServoOffCommand.RaiseCanExecuteChanged();
        ShutdownCommand.RaiseCanExecuteChanged();
        SwitchOnCommand.RaiseCanExecuteChanged();
        EnableOperationCommand.RaiseCanExecuteChanged();
        DisableVoltageCommand.RaiseCanExecuteChanged();
        FaultResetCommand.RaiseCanExecuteChanged();
    }
}
