using System.Management;
using System.Runtime.Versioning;

namespace CpuAffinityManager.Monitoring;

/// <summary>
/// WMI-based process creation monitor using Win32_ProcessStartTrace.
/// Provides real-time notifications when new processes start.
/// Runs on a dedicated background thread to avoid blocking WMI events.
/// </summary>
[SupportedOSPlatform("windows")]
public class WmiProcessMonitor : IProcessMonitor
{
    private ManagementEventWatcher? _watcher;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _watcher != null;
            }
        }
    }

    /// <summary>
    /// Starts the WMI process creation watcher. The callback is invoked
    /// on a ThreadPool thread after a short delay to allow process initialization.
    /// </summary>
    public void Start(Action<ProcessStartEvent> onProcessStarted)
    {
        lock (_lock)
        {
            if (_watcher != null)
                return; // Already running
        }

        try
        {
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_ProcessStartTrace");

            var watcher = new ManagementEventWatcher(query);

            watcher.EventArrived += (sender, args) =>
            {
                try
                {
                    var e = args.NewEvent;
                    int pid = Convert.ToInt32(e.Properties["ProcessID"].Value);
                    string processName = e.Properties["ProcessName"].Value?.ToString() ?? "";

                    // Delay to allow the process to fully initialize before applying rules
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            Thread.Sleep(200);
                            onProcessStarted(new ProcessStartEvent(pid, processName));
                        }
                        catch
                        {
                            // Callback exception should not crash the watcher
                        }
                    });
                }
                catch
                {
                    // Event parsing failure — skip this event
                }
            };

            watcher.Start();

            lock (_lock)
            {
                _watcher = watcher;
            }
        }
        catch (ManagementException)
        {
            // WMI may not be available or user lacks permissions
            // The monitor simply won't run — rules can still be applied manually
        }
    }

    /// <summary>
    /// Stops the WMI watcher. Can be restarted by calling Start again.
    /// </summary>
    public void Stop()
    {
        ManagementEventWatcher? watcher;
        lock (_lock)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher != null)
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
