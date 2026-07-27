using System;
using System.Threading;
using System.Threading.Tasks;
using CpuAffinityManager.Api;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using Serilog;

namespace CpuAffinityManager.Avalonia.Services;

/// <summary>
/// Hosts the third-party-AI HTTP API from inside the GUI so the user can turn it on
/// with a single switch (no separate command line needed). Wraps
/// <see cref="HttpApiServer"/> with start/stop lifecycle management.
/// </summary>
public sealed class ApiServerService
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcement;
    private readonly Action _persist;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; } = 8088;
    public bool AllowRemote { get; private set; }

    public string Url => $"http://{(AllowRemote ? "0.0.0.0" : "127.0.0.1")}:{Port}";

    public ApiServerService(
        IRuleEngine ruleEngine,
        ICpuTopologyService topoService,
        IEnforcementService enforcement,
        Action persist)
    {
        _ruleEngine = ruleEngine;
        _topoService = topoService;
        _enforcement = enforcement;
        _persist = persist;
    }

    /// <summary>
    /// Starts the API. Returns null on success or an error message on failure
    /// (e.g. port in use, or HttpListener needs elevation).
    /// </summary>
    public string? Start(int port, bool allowRemote)
    {
        if (IsRunning) return null;

        Port = port;
        AllowRemote = allowRemote;

        try
        {
            _cts = new CancellationTokenSource();
            var server = new HttpApiServer(_ruleEngine, _topoService, _enforcement, _persist, port, allowRemote);
            _runTask = server.RunAsync(_cts.Token);
            IsRunning = true;
            Log.Information("In-app HTTP API started on {Url}", Url);
            return null;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Log.Error(ex, "Failed to start in-app HTTP API");
            return ex.Message;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts?.Cancel(); } catch { }
        IsRunning = false;
        Log.Information("In-app HTTP API stopped");
    }
}
