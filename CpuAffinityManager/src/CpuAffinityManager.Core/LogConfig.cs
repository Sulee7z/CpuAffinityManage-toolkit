using Serilog;
using Serilog.Events;

namespace CpuAffinityManager;

/// <summary>
/// Shared Serilog initialization for all projects (WPF, Avalonia, MCP).
/// AOT-compatible: uses code-based config only, no reflection/config files.
/// Call once at startup — subsequent calls are no-ops.
/// </summary>
public static class LogConfig
{
    private static bool _initialized;

    /// <summary>
    /// Initialize the global static Serilog logger.
    /// </summary>
    /// <param name="appName">Short app identifier for log filename (e.g. "wpf", "av", "mcp").</param>
    /// <param name="logDir">Directory for log files. Created if needed.</param>
    public static void Initialize(string appName, string? logDir = null)
    {
        if (_initialized) return;
        _initialized = true;

        // Logs live under %LOCALAPPDATA%\CpuAffinityManager\logs, never the install
        // directory — writing logs into a Program Files / Windows-protected folder
        // fails without elevation and can leave permission-locked files behind.
        logDir ??= Path.Combine(Engine.RuleConfigPath.DataDirectory, "logs");
        try { Directory.CreateDirectory(logDir); } catch { }
        string logPath = Path.Combine(logDir, $"cpu-affinity-{appName}-.log");

        // File-only sink. The IDE/Debug output sink was removed for release builds —
        // it added overhead and pulled in Serilog.Sinks.Debug with no user-facing value.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("── Serilog initialized for {App} (AOT-safe) ──", appName);
        Log.Information("Logs: {Path}", logPath);
    }

    /// <summary>
    /// Flush and close the log. Call on app shutdown.
    /// </summary>
    public static void Shutdown()
    {
        Log.Information("── Shutdown ──");
        Log.CloseAndFlush();
    }
}
