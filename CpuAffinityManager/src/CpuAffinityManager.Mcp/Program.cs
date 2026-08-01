using CpuAffinityManager.Mcp;

// CPU Affinity Manager — MCP Server / HTTP API
//
// Default (no args): MCP server over stdio (JSON-RPC on stdin/stdout) for AI agents.
//   CpuAffinityManager.Mcp.exe
//
// HTTP REST API (for third-party AI or any HTTP client):
//   CpuAffinityManager.Mcp.exe --http            (listens on 127.0.0.1:8088)
//   CpuAffinityManager.Mcp.exe --http 9000       (custom port)
//   CpuAffinityManager.Mcp.exe --http 9000 --allow-remote   (bind all interfaces)
//
// All logging goes to the log file to avoid corrupting the JSON stream.

var server = new McpServer();

bool useHttp = args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase));
if (useHttp)
{
    int port = 8088;
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--http", StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
        {
            port = p;
        }
    }
    bool allowRemote = args.Any(a => string.Equals(a, "--allow-remote", StringComparison.OrdinalIgnoreCase));

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await server.RunHttpApiAsync(port, allowRemote, cts.Token);
}
else
{
    await server.RunAsync();
}
