using Microsoft.Extensions.Hosting;
using Serilog;

namespace CpuAffinityManager.Configuration;

public class ConfigInitService : IHostedService
{
    private readonly ConfigManager _configManager;

    public ConfigInitService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("Initializing configuration...");
        await _configManager.InitializeAsync().ConfigureAwait(false);
        await _configManager.EnsureDefaultsAsync().ConfigureAwait(false);
        Log.Information("Configuration initialized");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
