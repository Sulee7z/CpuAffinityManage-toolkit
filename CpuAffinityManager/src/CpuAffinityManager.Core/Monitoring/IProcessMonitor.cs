namespace CpuAffinityManager.Monitoring;

/// <summary>
/// Event data for a newly started process.
/// </summary>
/// <param name="Pid">Process ID.</param>
/// <param name="ProcessName">Executable name (e.g., "game.exe").</param>
public record ProcessStartEvent(int Pid, string ProcessName);

/// <summary>
/// Monitors for new process creation events. WMI-based implementation
/// uses Win32_ProcessStartTrace queries.
/// </summary>
public interface IProcessMonitor : IDisposable
{
    /// <summary>
    /// Starts monitoring for new processes. Calls the callback on each new process.
    /// </summary>
    /// <param name="onProcessStarted">Callback invoked when a new process starts.</param>
    void Start(Action<ProcessStartEvent> onProcessStarted);

    /// <summary>
    /// Stops monitoring. Can be restarted.
    /// </summary>
    void Stop();

    /// <summary>
    /// Whether the monitor is currently active.
    /// </summary>
    bool IsRunning { get; }
}
