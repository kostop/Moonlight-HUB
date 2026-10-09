namespace MoonlightHub.Core;

/// <summary>A running deskflow-core process (or the touch-filter wrapper that runs it on Windows).</summary>
public sealed record CoreProcess(int Pid, DeskflowRole Role, bool IsWrapper, string Path, string CommandLine);

/// <summary>A TCP connection of the Deskflow port, keyed so that its age can be tracked between observations.</summary>
public sealed record CoreConnection(string Key, string RemoteAddress);

/// <summary>
/// What differs between the systems: where Deskflow lives, how the core is started and stopped, how its connections are
/// seen, and which of the old autostart pieces the hub replaces. <see cref="DeskflowManager"/> holds everything else
/// (role, guard, shared layout, peer link).
/// </summary>
public interface IDeskflowHost
{
    /// <summary>Deskflow.conf the core reads (coreMode, remoteHost, computerName, port, TLS, log).</summary>
    string ConfigPath { get; }
    /// <summary>File the core writes its log to (configured through Deskflow.conf by <see cref="Start"/>).</summary>
    string CoreLogPath { get; }
    bool Installed { get; }
    /// <summary>Install location shown in the UI.</summary>
    string InstallLocation { get; }
    /// <summary>Server address of the shared layout service (deskflow-layout-web) for the given peer.</summary>
    string LayoutHost(string peerHost);

    List<CoreProcess> FindCores();
    /// <summary>Starts a core in the given role; returns the started process id (0 when unknown) or throws.</summary>
    int Start(DeskflowRole role, DeskflowSettings settings);
    void Stop(IReadOnlyCollection<CoreProcess> cores);
    /// <summary>Listening state of the Deskflow port and the established connections of the running cores.</summary>
    (bool Listening, List<CoreConnection> Connections) Connections(int port, DeskflowRole role, IReadOnlyCollection<int> corePids);
    /// <summary>Touch filter health (Windows only; null elsewhere or when not checked).</summary>
    bool? CheckFilter(IReadOnlyCollection<CoreProcess> cores);
    bool F24RulePresent();
    bool MonitorRunning();
    /// <summary>The stand-alone touch isolator (bridge mode) is running (Windows only).</summary>
    bool TouchBridgeRunning();
    /// <summary>Called before the core is stopped for a role change: keeps tablet touch isolated while no filtered core runs.</summary>
    void PrepareForCoreRestart(DeskflowSettings settings);

    /// <summary>Per-tick upkeep besides the core itself (monitor, side-channel processes, layout service, priority).</summary>
    void Tick(DeskflowSettings settings, DeskflowStatus status);
    /// <summary>Disables Deskflow's own autostart / background pieces that the hub now replaces. Returns notes.</summary>
    List<string> TakeOverLegacy(DeskflowSettings settings);
    /// <summary>Re-enables them (rollback). Returns notes.</summary>
    List<string> RestoreLegacy(DeskflowSettings settings);
    /// <summary>Short text about the old autostart pieces for the UI (may be slow: call off the UI thread).</summary>
    string LegacyStatus();
    /// <summary>Stops background helpers of the host (pipe emulation, supervisors) without touching the core.</summary>
    void Detach();
}
