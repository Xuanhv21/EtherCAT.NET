using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using EtherCAT.NET.Engine.Discovery;
using EtherCAT.NET.Engine.Esc;
using EtherCAT.NET.Engine.Esi;
using EtherCAT.NET.Engine.ProcessData;
using EtherCAT.NET.Engine.Protocol;
using EtherCAT.NET.Engine.StateMachine;
using EtherCAT.NET.Mvvm;
using EtherCAT.NET.Transport.Pcap;

namespace EtherCAT.NET;

/// <summary>
/// The Milestone 1 bring-up view model described in the implementation plan's "UI WPF" section.
/// Owns the whole master pipeline for one adapter/one slave: opens
/// <see cref="PcapEthernetFrameTransport"/> for the selected <see cref="AdapterInfo"/>, runs
/// <see cref="SlaveDiscovery.DiscoverSingleSlave"/> against the embedded Panasonic MINAS A6BE ESI
/// descriptor, computes the process-image plan with <see cref="ProcessImageBuilder"/>, drives
/// <see cref="AlStateMachine"/> INIT-&gt;PREOP-&gt;SAFEOP-&gt;OP (wiring its <c>onSafeOpRequested</c>
/// hook to start <see cref="CyclicExchangeService"/> at exactly the right instant, per
/// <see cref="AlStateMachine"/>'s own remarks), and thereafter exposes the cyclic exchange's
/// Statusword bits, AL state, and log as bindable properties.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading:</b> this type is constructed on the UI thread, where it captures
/// <see cref="SynchronizationContext.Current"/>. Every property this class mutates from
/// <see cref="CyclicExchangeService.StatusUpdated"/>/<see cref="CyclicExchangeService.LogEmitted"/>
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

    private readonly SynchronizationContext? _uiContext;
    private int _lifecycleState = LifecycleIdle;

    private PcapEthernetFrameTransport? _transport;
    private CyclicExchangeService? _cyclicExchange;

    private AdapterInfo? _selectedAdapter;
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

    /// <summary>Creates the view model. Must be constructed on the UI thread — its constructor captures <see cref="SynchronizationContext.Current"/> for later marshaling.</summary>
    public MainWindowViewModel()
    {
        _uiContext = SynchronizationContext.Current;

        Adapters = [];
        LogEntries = [];

        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => CanStop);
        RefreshAdaptersCommand = new RelayCommand(LoadAdapters, () => !IsBusy && !IsRunning);

        ShutdownCommand = new RelayCommand(() => SendControlword("Shutdown", Ds402Controlword.Shutdown), () => IsRunning);
        SwitchOnCommand = new RelayCommand(() => SendControlword("Switch On", Ds402Controlword.SwitchOn), () => IsRunning);
        EnableOperationCommand = new RelayCommand(() => SendControlword("Enable Operation", Ds402Controlword.EnableOperation), () => IsRunning);
        DisableVoltageCommand = new RelayCommand(() => SendControlword("Disable Voltage", Ds402Controlword.DisableVoltage), () => IsRunning);
        FaultResetCommand = new RelayCommand(() => SendControlword("Fault Reset", Ds402Controlword.FaultReset), () => IsRunning);

        LoadAdapters();
    }

    /// <summary>Every adapter <see cref="PcapAdapters.GetAvailableAdapters()"/> reported the last time it was called — never throws, never null, empty when Npcap is missing.</summary>
    public ObservableCollection<AdapterInfo> Adapters { get; }

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

    /// <summary>Re-enumerates <see cref="Adapters"/>. Never throws, per <see cref="PcapAdapters.GetAvailableAdapters(out string?)"/>.</summary>
    public RelayCommand RefreshAdaptersCommand { get; }

    /// <summary>Opens the selected adapter and runs the full discovery -&gt; PREOP -&gt; SAFEOP -&gt; OP bring-up.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Stops the cyclic exchange (sending one final Disable Voltage cycle first, per <see cref="CyclicExchangeService.Stop"/>) and closes the transport.</summary>
    public RelayCommand StopCommand { get; }

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

    private bool CanStart => !IsBusy && !IsRunning && SelectedAdapter is not null;

    private bool CanStop => !IsBusy && IsRunning;

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
            Interlocked.Exchange(ref _lifecycleState, LifecycleIdle);
        }
    }

    /// <summary>
    /// Runs on a thread pool thread (never the UI thread): opens the transport, discovers the
    /// slave, builds the process-image plan, and drives <see cref="AlStateMachine"/> all the way to
    /// OP, wiring its <c>onSafeOpRequested</c> hook to start <see cref="CyclicExchangeService"/> at
    /// exactly the point the plan's remarks describe. On any failure, disposes whatever was opened
    /// and rethrows so <see cref="StartAsync"/> can report it.
    /// </summary>
    private void RunBringUp(AdapterInfo adapter)
    {
        var transport = new PcapEthernetFrameTransport(adapter);
        CyclicExchangeService? cyclic = null;

        try
        {
            var escClient = new EscClient(transport, SourceMac);
            var esiLibrary = EsiXmlParser.ParseEmbeddedPanasonicMinasA6Be();

            AppendLog("Discovering slave(s) on the bus...");
            var discovery = SlaveDiscovery.DiscoverSingleSlave(escClient, esiLibrary);
            AppendLog($"Discovered {discovery.SlaveCount} slave(s); matched '{discovery.Device.Name}' " +
                      $"(ProductCode=0x{discovery.Device.ProductCode:X8}, Revision=0x{discovery.Device.RevisionNumber:X8}), " +
                      $"assigned Configured Station Address 0x{discovery.StationAddress:X4}.");

            var plan = ProcessImageBuilder.BuildDefault(discovery.Device);

            var stateMachine = new AlStateMachine(escClient, transport, SourceMac, discovery.StationAddress);
            cyclic = new CyclicExchangeService(transport, SourceMac, plan);
            cyclic.StatusUpdated += OnStatusUpdated;
            cyclic.LogEmitted += AppendLog;

            AppendLog("Requesting PREOP...");
            stateMachine.TransitionToPreOp(discovery.Device);
            cyclic.SetAlState(AlState.PreOp);
            AppendLog("Reached PREOP.");

            AppendLog("Requesting SAFEOP and starting cyclic exchange...");
            stateMachine.TransitionToSafeOp(discovery.Device, plan, onSafeOpRequested: cyclic.Start);
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
            // onSafeOpRequested hook, per AlStateMachine's own remarks) even though a *later* step
            // (e.g. TransitionToOp's readiness wait) is what actually failed -- so an already-live
            // cyclic thread must be stopped cleanly (its own final Disable Voltage exchange still
            // going out over the still-open transport) before the transport is disposed, rather
            // than left running to fail on its own against a disposed transport.
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
    /// <see cref="CyclicExchangeService.StatusUpdated"/> handler — runs on the cyclic background
    /// thread. Marshals every bound-property write onto the UI thread via <see cref="RunOnUiThread"/>.
    /// </summary>
    private void OnStatusUpdated(ProcessImageSnapshot snapshot)
    {
        RunOnUiThread(() =>
        {
            AlStateText = snapshot.IsFaulted ? $"{snapshot.AlState} (FAULTED)" : snapshot.AlState.ToString();

            ReadyToSwitchOn = snapshot.Status.ReadyToSwitchOn;
            SwitchedOn = snapshot.Status.SwitchedOn;
            OperationEnabled = snapshot.Status.OperationEnabled;
            Fault = snapshot.Status.Fault;
            VoltageEnabled = snapshot.Status.VoltageEnabled;
            QuickStop = snapshot.Status.QuickStop;
            SwitchOnDisabled = snapshot.Status.SwitchOnDisabled;
            Warning = snapshot.Status.Warning;
            IsDataFresh = snapshot.IsDataFresh;
        });
    }

    private void SendControlword(string name, ushort value)
    {
        _cyclicExchange?.SetControlword(value);
        AppendLog($"Controlword -> {name} (0x{value:X4}).");
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
        ShutdownCommand.RaiseCanExecuteChanged();
        SwitchOnCommand.RaiseCanExecuteChanged();
        EnableOperationCommand.RaiseCanExecuteChanged();
        DisableVoltageCommand.RaiseCanExecuteChanged();
        FaultResetCommand.RaiseCanExecuteChanged();
    }
}
