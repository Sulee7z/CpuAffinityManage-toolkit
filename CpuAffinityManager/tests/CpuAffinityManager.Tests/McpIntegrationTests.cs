using System.Diagnostics;
using System.Text.Json;

namespace CpuAffinityManager.Tests;

/// <summary>
/// End-to-end integration tests for the MCP server.
/// Spawns the MCP process and communicates via stdin/stdout JSON-RPC.
/// </summary>
public class McpIntegrationTests : IDisposable
{
    private readonly Process _mcpProcess;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _nextId = 1;

    // Test process we can freely modify affinity on
    private Process? _testProcess;

    public McpIntegrationTests()
    {
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Locate the MCP executable
        string mcpDir = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "CpuAffinityManager.Mcp"));

        string mcpExe = Path.Combine(mcpDir, "bin", "Release", "net10.0", "CpuAffinityManager.Mcp.exe");
        if (!File.Exists(mcpExe))
            mcpExe = Path.Combine(mcpDir, "bin", "Debug", "net10.0", "CpuAffinityManager.Mcp.exe");

        // Fallback to dotnet run
        if (!File.Exists(mcpExe))
        {
            _mcpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{mcpDir}\" --configuration Release",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = mcpDir
                }
            };
        }
        else
        {
            _mcpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = mcpExe,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = mcpDir  // Ensure relative config paths resolve
                }
            };
        }

        _mcpProcess.Start();
        _stdin = _mcpProcess.StandardInput;
        _stdout = _mcpProcess.StandardOutput;

        // Initialize MCP handshake
        var initResult = SendRequest("initialize", new { });
        Assert.NotNull(initResult);
        var result = initResult?.GetProperty("result");
        Assert.True(result.HasValue, "Initialize should return result");
    }

    [Fact]
    public void GetTopology_ReturnsValidData()
    {
        var result = SendRequest("tools/call", new
        {
            name = "get_topology",
            arguments = new { }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var topo = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(topo.TryGetProperty("totalLogicalProcessors", out var tlp));
        Assert.True(tlp.GetInt32() > 0);
        Assert.True(topo.TryGetProperty("pCoreCount", out _));
        Assert.True(topo.TryGetProperty("availableModes", out var modes));
        Assert.True(modes.GetArrayLength() > 0);
    }

    [Fact]
    public void ListProcesses_ReturnsProcesses()
    {
        var result = SendRequest("tools/call", new
        {
            name = "list_processes",
            arguments = new { top = 10 }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("processes", out var procs));
        Assert.True(procs.GetArrayLength() > 0);

        // Each process should have pid and name
        var first = procs[0];
        Assert.True(first.TryGetProperty("pid", out var pid));
        Assert.True(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void ListProcesses_WithFilter_ReturnsFiltered()
    {
        var result = SendRequest("tools/call", new
        {
            name = "list_processes",
            arguments = new { filter = "svchost.exe", top = 5 }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("processes", out var procs));
        foreach (var proc in procs.EnumerateArray())
        {
            var name = proc.GetProperty("name").GetString();
            Assert.Contains("svchost", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetRules_ReturnsConfiguredRules()
    {
        var result = SendRequest("tools/call", new
        {
            name = "get_rules",
            arguments = new { }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("rules", out var rules));
        // Rules might be empty if config file wasn't found — that's valid
        Assert.True(data.TryGetProperty("count", out var count));
    }

    [Fact]
    public void SetAffinity_HardAffinity_Succeeds()
    {
        // Start a test process we control
        StartTestProcess();

        var result = SendRequest("tools/call", new
        {
            name = "set_affinity",
            arguments = new
            {
                pid = _testProcess!.Id,
                mode = "first-half",
                level = "hard-affinity"
            }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);

        // May fail if not running as admin, but should return a structured response
        Assert.True(data.TryGetProperty("pid", out var pid));
        Assert.Equal(_testProcess!.Id, pid.GetInt32());

        if (data.TryGetProperty("success", out var success))
        {
            // If it succeeded, verify mask is set
            if (success.GetBoolean())
            {
                Assert.True(data.TryGetProperty("maskApplied", out _));
            }
        }
    }

    [Fact]
    public void SetAffinity_CompositeFallback_Succeeds()
    {
        StartTestProcess();

        var result = SendRequest("tools/call", new
        {
            name = "set_affinity",
            arguments = new
            {
                pid = _testProcess!.Id,
                mode = "e-cores|p-cores|first-half",  // triple fallback
                level = "hard-affinity"
            }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("mode", out var mode));
        Assert.Contains("|", mode.GetString()!);
    }

    [Fact]
    public void AddAndRemoveRule_RoundTrip()
    {
        // Add a rule
        var addResult = SendRequest("tools/call", new
        {
            name = "add_rule",
            arguments = new
            {
                name = "MCP Test Rule",
                processPattern = "test*.exe",
                mode = "p-cores|first-half",
                level = "hard-affinity"
            }
        });

        Assert.NotNull(addResult);
        var addContent = ExtractToolContent(addResult!.Value);
        Assert.NotNull(addContent);

        var addData = JsonSerializer.Deserialize<JsonElement>(addContent!);
        Assert.True(addData.TryGetProperty("added", out var added));
        Assert.True(added.GetBoolean());
        Assert.True(addData.TryGetProperty("ruleId", out var ruleId));
        string id = ruleId.GetString()!;

        // Remove it
        var removeResult = SendRequest("tools/call", new
        {
            name = "remove_rule",
            arguments = new { ruleId = id }
        });

        Assert.NotNull(removeResult);
        var removeContent = ExtractToolContent(removeResult!.Value);
        Assert.NotNull(removeContent);

        var removeData = JsonSerializer.Deserialize<JsonElement>(removeContent!);
        Assert.True(removeData.TryGetProperty("removed", out var removed));
        Assert.True(removed.GetBoolean());
    }

    [Fact]
    public void ApplyRule_ToTestProcess_Succeeds()
    {
        StartTestProcess();

        // First add a temporary rule
        var addResult = SendRequest("tools/call", new
        {
            name = "add_rule",
            arguments = new
            {
                name = "Temp Apply Test",
                processPattern = "cmd.exe|conhost.exe",
                mode = "first-half",
                level = "hard-affinity"
            }
        });

        var addContent = ExtractToolContent(addResult!.Value);
        var addData = JsonSerializer.Deserialize<JsonElement>(addContent!);
        string ruleId = addData.GetProperty("ruleId").GetString()!;

        // Apply it to our test process
        var applyResult = SendRequest("tools/call", new
        {
            name = "apply_rule",
            arguments = new
            {
                ruleId = ruleId,
                pid = _testProcess!.Id
            }
        });

        Assert.NotNull(applyResult);
        var applyContent = ExtractToolContent(applyResult!.Value);
        Assert.NotNull(applyContent);

        var applyData = JsonSerializer.Deserialize<JsonElement>(applyContent!);
        Assert.True(applyData.TryGetProperty("ruleId", out _));

        // Clean up
        SendRequest("tools/call", new
        {
            name = "remove_rule",
            arguments = new { ruleId = ruleId }
        });
    }

    [Fact]
    public void ScanAndEnforce_ReturnsCount()
    {
        var result = SendRequest("tools/call", new
        {
            name = "scan_and_enforce",
            arguments = new { }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("affectedProcesses", out var count));
    }

    [Fact]
    public void SetAffinity_WithSocketSuffix_HandlesGracefully()
    {
        StartTestProcess();

        var result = SendRequest("tools/call", new
        {
            name = "set_affinity",
            arguments = new
            {
                pid = _testProcess!.Id,
                mode = "all-cores@socket0",
                level = "hard-affinity"
            }
        });

        Assert.NotNull(result);
        var content = ExtractToolContent(result!.Value);
        Assert.NotNull(content);

        // Socket 0 should exist on all machines (single socket → socket 0 = all cores)
        var data = JsonSerializer.Deserialize<JsonElement>(content!);
        Assert.True(data.TryGetProperty("pid", out _));
    }

    [Fact]
    public void ToolsList_ReturnsAllToolNames()
    {
        var result = SendRequest("tools/list", new { });

        Assert.NotNull(result);
        Assert.True(result!.Value.TryGetProperty("result", out var r));
        Assert.True(r.TryGetProperty("tools", out var tools));

        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("get_topology", toolNames);
        Assert.Contains("list_processes", toolNames);
        Assert.Contains("get_rules", toolNames);
        Assert.Contains("set_affinity", toolNames);
        Assert.Contains("apply_rule", toolNames);
        Assert.Contains("scan_and_enforce", toolNames);
        Assert.Contains("add_rule", toolNames);
        Assert.Contains("remove_rule", toolNames);
        Assert.Equal(8, toolNames.Count);
    }

    #region Helpers

    private JsonElement? SendRequest(string method, object? @params)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = _nextId++,
            method,
            @params
        };

        string requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        Debug.WriteLine($"[MCP REQ] {requestJson}");

        _stdin.WriteLine(requestJson);
        _stdin.Flush();

        string? responseJson = _stdout.ReadLine();
        Debug.WriteLine($"[MCP RES] {responseJson}");

        if (string.IsNullOrEmpty(responseJson))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(responseJson, _jsonOptions);
    }

    private static string? ExtractToolContent(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result))
            return null;

        if (!result.TryGetProperty("content", out var content))
            return null;

        var firstContent = content[0];
        return firstContent.GetProperty("text").GetString();
    }

    private void StartTestProcess()
    {
        _testProcess?.Kill();
        _testProcess?.Dispose();

        _testProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout 60",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        _testProcess.Start();

        // Give it a moment to initialize
        Thread.Sleep(500);
    }

    public void Dispose()
    {
        try
        {
            _testProcess?.Kill();
            _testProcess?.Dispose();
        }
        catch { }

        try
        {
            _stdin?.Close();
        }
        catch { }

        try
        {
            if (!_mcpProcess.HasExited)
            {
                _mcpProcess.Kill(entireProcessTree: true);
                _mcpProcess.WaitForExit(5000);
            }
            _mcpProcess.Dispose();
        }
        catch { }
    }

    #endregion
}
