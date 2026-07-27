using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CpuAffinityManager;
using CpuAffinityManager.Api;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using Serilog;

namespace CpuAffinityManager.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server using stdio transport.
/// Exposes CPU affinity tools to AI agents.
/// </summary>
public class McpServer
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcementService;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string ServerName = "cpu-affinity-manager";
    private const string ServerVersion = "1.0.0";

    public McpServer()
    {
        // Init shared logger (no-op if already initialized)
        LogConfig.Initialize("mcp");

        _ruleEngine = new RuleEngine();
        _topoService = new CpuTopologyService();
        _enforcementService = new EnforcementService(_ruleEngine, _topoService);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        // Load default rules
        try
        {
            string configPath = RuleConfigPath.FindDefaultRules();
            if (File.Exists(configPath))
                _ruleEngine.Load(configPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load rules");
        }
    }

    /// <summary>Persists the current rules to the writable rules file.</summary>
    private void SaveRules()
    {
        try { _ruleEngine.Save(RuleConfigPath.FindDefaultRules()); }
        catch (Exception ex) { Log.Warning(ex, "Could not save rules"); }
    }

    /// <summary>
    /// Runs the HTTP REST API (for third-party AI agents) instead of stdio MCP.
    /// </summary>
    public async Task RunHttpApiAsync(int port, bool allowRemote, CancellationToken ct = default)
    {
        var api = new HttpApiServer(_ruleEngine, _topoService, _enforcementService, SaveRules, port, allowRemote);
        Log.Information("Starting HTTP API on port {Port} (allowRemote={Remote})", port, allowRemote);
        await api.RunAsync(ct);
    }

    public async Task RunAsync()
    {
        Log.Information("{Name} v{Version} MCP server starting on stdio", ServerName, ServerVersion);

        using var reader = new StreamReader(Console.OpenStandardInput());
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null) break; // EOF

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, _jsonOptions);
                if (request == null) continue;

                var response = await HandleRequestAsync(request);
                string responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                await writer.WriteLineAsync(responseJson);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing MCP request");
                var errorResponse = new JsonRpcResponse
                {
                    Id = null,
                    Error = new JsonRpcError
                    {
                        Code = -32603,
                        Message = $"Internal error: {ex.Message}"
                    }
                };
                string errorJson = JsonSerializer.Serialize(errorResponse, _jsonOptions);
                await writer.WriteLineAsync(errorJson);
            }
        }

        Log.Information("MCP server shutting down");
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                return HandleInitialize(request);

            case "tools/list":
                return HandleToolsList(request);

            case "tools/call":
                return await HandleToolsCallAsync(request);

            default:
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32601,
                        Message = $"Method not found: {request.Method}"
                    }
                };
        }
    }

    #region MCP Protocol Handlers

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToElement(new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = ServerName,
                    version = ServerVersion
                }
            })
        };
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        var tools = new object[]
        {
            new
            {
                name = "get_topology",
                description = "Get the CPU topology of the current system — P-cores, E-cores, SMT, sockets, and available affinity modes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "list_drives",
                description = "List all fixed drive roots (e.g. C:\\, D:\\). Use to build cross-drive path rules. Tip: a path pattern starting with ** matches any drive.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "list_processes",
                description = "List running processes with PID, name, path, and current CPU affinity. Supports optional name filter.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filter = new
                        {
                            type = "string",
                            description = "Optional wildcard pattern to filter process names (e.g. \"chrome*\")"
                        },
                        top = new
                        {
                            type = "integer",
                            description = "Maximum number of processes to return (default: 50, max: 200)"
                        }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "get_rules",
                description = "Get all configured CPU affinity rules.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "set_affinity",
                description = "Set CPU affinity for a process by PID. Supports composite modes (e.g. 'p-cores|first-half'), socket filtering ('@socket0'), and custom hex masks.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        pid = new
                        {
                            type = "integer",
                            description = "Process ID to target"
                        },
                        mode = new
                        {
                            type = "string",
                            description = "Affinity mode: all-cores, p-cores, e-cores, p-cores-smt, p-cores-no-smt, first-half, second-half, custom. Supports fallback chain with | (e.g. 'p-cores|first-half') and socket suffix (e.g. 'p-cores@socket0')."
                        },
                        level = new
                        {
                            type = "string",
                            description = "Enforcement level: soft-cpu-sets, hard-affinity, job-enforced, job-locked. Default: hard-affinity"
                        },
                        customMask = new
                        {
                            type = "string",
                            description = "Hex affinity mask (e.g. '0xFF'). Only used when mode='custom'."
                        },
                        socketIndex = new
                        {
                            type = "integer",
                            description = "Optional physical CPU socket index (0-based). Restricts affinity to that socket."
                        }
                    },
                    required = new[] { "pid", "mode" }
                }
            },
            new
            {
                name = "apply_rule",
                description = "Apply a configured rule by its ID to a specific process.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ruleId = new
                        {
                            type = "string",
                            description = "ID of the rule to apply"
                        },
                        pid = new
                        {
                            type = "integer",
                            description = "Process ID to apply the rule to"
                        }
                    },
                    required = new[] { "ruleId", "pid" }
                }
            },
            new
            {
                name = "scan_and_enforce",
                description = "Scan all running processes and apply matching rules. Returns count of affected processes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "add_rule",
                description = "Add or update a CPU affinity rule.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "Rule name"
                        },
                        processPattern = new
                        {
                            type = "string",
                            description = "Process name wildcard (e.g. 'game*.exe')"
                        },
                        pathPattern = new
                        {
                            type = "string",
                            description = "Optional path wildcard (e.g. 'D:\\Games\\**')"
                        },
                        mode = new
                        {
                            type = "string",
                            description = "Affinity mode. Supports fallback: 'p-cores|first-half'"
                        },
                        level = new
                        {
                            type = "string",
                            description = "Enforcement level: soft-cpu-sets, hard-affinity, job-enforced, job-locked"
                        },
                        socketIndex = new
                        {
                            type = "integer",
                            description = "Optional socket index for multi-CPU systems"
                        },
                        lockBreakaway = new
                        {
                            type = "boolean",
                            description = "Prevent process from escaping Job Object"
                        }
                    },
                    required = new[] { "name", "processPattern", "mode", "level" }
                }
            },
            new
            {
                name = "remove_rule",
                description = "Remove a rule by its ID.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ruleId = new
                        {
                            type = "string",
                            description = "ID of the rule to remove"
                        }
                    },
                    required = new[] { "ruleId" }
                }
            }
        };

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToElement(new { tools })
        };
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request)
    {
        var paramsElement = request.Params;
        string? toolName = paramsElement?.GetProperty("name").GetString();
        var args = paramsElement?.GetProperty("arguments");

        if (string.IsNullOrEmpty(toolName))
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32602, Message = "Missing tool name" }
            };
        }

        try
        {
            var result = toolName switch
            {
                "get_topology" => ExecuteGetTopology(),
                "list_drives" => ExecuteListDrives(),
                "list_processes" => ExecuteListProcesses(args),
                "get_rules" => ExecuteGetRules(),
                "set_affinity" => ExecuteSetAffinity(args),
                "apply_rule" => ExecuteApplyRule(args),
                "scan_and_enforce" => ExecuteScanAndEnforce(),
                "add_rule" => ExecuteAddRule(args),
                "remove_rule" => ExecuteRemoveRule(args),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
            };

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JsonSerializer.SerializeToElement(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonSerializer.Serialize(result, _jsonOptions)
                        }
                    }
                })
            };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JsonSerializer.SerializeToElement(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonSerializer.Serialize(new { error = ex.Message }, _jsonOptions)
                        }
                    },
                    isError = true
                })
            };
        }
    }

    #endregion

    #region Tool Executors

    private object ExecuteGetTopology()
    {
        var topo = _topoService.Detect();
        return new
        {
            totalLogicalProcessors = topo.TotalLogicalProcessors,
            pCoreCount = topo.PcoreCount,
            eCoreCount = topo.EcoreCount,
            smtEnabled = topo.SmtEnabled,
            socketCount = topo.SocketCount,
            socketMasks = topo.SocketMasks.Select(m => $"0x{m:X}").ToList(),
            pCoreMask = $"0x{topo.PcoreMask:X}",
            eCoreMask = $"0x{topo.EcoreMask:X}",
            smt0Mask = $"0x{topo.Smt0Mask:X}",
            smt1Mask = $"0x{topo.Smt1Mask:X}",
            availableModes = ICpuTopologyService.AvailableModes,
            compositeExample = "Use | for fallback chains (e.g. 'p-cores|first-half') and @socketN for socket filtering (e.g. 'p-cores@socket0')"
        };
    }

    private object ExecuteListProcesses(JsonElement? args)
    {
        string? filter = args?.TryGetProperty("filter", out var f) == true ? f.GetString() : null;
        int top = args?.TryGetProperty("top", out var t) == true && t.TryGetInt32(out int val) ? Math.Min(val, 200) : 50;

        var processes = new List<object>();
        foreach (var proc in Process.GetProcesses())
        {
            if (processes.Count >= top) break;

            try
            {
                string name = proc.ProcessName + ".exe";
                if (filter != null && !Engine.Wildcard.Match(name, filter, true))
                    continue;

                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }

                processes.Add(new
                {
                    pid = proc.Id,
                    name,
                    path = path ?? "(protected)",
                    affinity = $"0x{proc.ProcessorAffinity.ToInt64():X}",
                    priorityClass = proc.PriorityClass.ToString()
                });
            }
            catch
            {
                // skip inaccessible processes
            }
        }

        return new { count = processes.Count, processes };
    }

    private object ExecuteGetRules()
    {
        var rules = _ruleEngine.Rules.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            enabled = r.Enabled,
            processPattern = r.Match.Process,
            pathPattern = r.Match.Path,
            mode = r.Action.Mode,
            level = r.Action.Level,
            socketIndex = r.Action.SocketIndex,
            lockBreakaway = r.Action.Lock,
            customMask = r.Action.CustomMask
        }).ToList();

        return new { count = rules.Count, rules };
    }

    private object ExecuteSetAffinity(JsonElement? args)
    {
        if (args == null) throw new ArgumentException("Missing arguments");

        int pid = args.Value.GetProperty("pid").GetInt32();
        string mode = args.Value.GetProperty("mode").GetString()!;
        string level = "hard-affinity";
        if (args.Value.TryGetProperty("level", out var l))
            level = l.GetString()!;

        string? customMask = null;
        if (args.Value.TryGetProperty("customMask", out var cm))
            customMask = cm.GetString();

        int? socketIndex = null;
        if (args.Value.TryGetProperty("socketIndex", out var si) && si.TryGetInt32(out int siVal))
            socketIndex = siVal;

        var topology = _topoService.Detect();

        // Build final mode string with socket suffix
        string finalMode = mode;
        if (socketIndex.HasValue && socketIndex.Value >= 0)
            finalMode += $"@socket{socketIndex.Value}";

        ulong mask = CpuTopology.BuildMask(topology, finalMode, customMask != null ? ParseHex(customMask) : null);

        if (mask == 0)
            throw new InvalidOperationException($"Could not build mask for mode '{finalMode}'. The mode may not match any cores on this system.");

        // Apply using EnforcementService
        var rule = new RuleEntry
        {
            Id = "adhoc",
            Name = "Ad-hoc MCP call",
            Action = new RuleAction
            {
                Mode = mode,
                Level = level,
                CustomMask = customMask,
                SocketIndex = socketIndex
            }
        };

        bool success = _enforcementService.Apply(pid, rule, topology);

        return new
        {
            success,
            pid,
            mode = finalMode,
            level,
            maskApplied = $"0x{mask:X}",
            maskDecimal = mask
        };
    }

    private object ExecuteApplyRule(JsonElement? args)
    {
        if (args == null) throw new ArgumentException("Missing arguments");

        string ruleId = args.Value.GetProperty("ruleId").GetString()!;
        int pid = args.Value.GetProperty("pid").GetInt32();

        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null)
            throw new InvalidOperationException($"Rule '{ruleId}' not found.");

        var topology = _topoService.Detect();
        bool success = _enforcementService.Apply(pid, rule, topology);

        return new
        {
            success,
            pid,
            ruleId,
            ruleName = rule.Name,
            mode = rule.Action.Mode,
            level = rule.Action.Level
        };
    }

    private object ExecuteScanAndEnforce()
    {
        int count = _enforcementService.ScanAndEnforce();
        return new { affectedProcesses = count };
    }

    private object ExecuteAddRule(JsonElement? args)
    {
        if (args == null) throw new ArgumentException("Missing arguments");

        string name = args.Value.GetProperty("name").GetString()!;
        string processPattern = args.Value.GetProperty("processPattern").GetString()!;
        string mode = args.Value.GetProperty("mode").GetString()!;
        string level = args.Value.GetProperty("level").GetString()!;

        string? pathPattern = null;
        if (args.Value.TryGetProperty("pathPattern", out var pp))
            pathPattern = pp.GetString();

        int? socketIndex = null;
        if (args.Value.TryGetProperty("socketIndex", out var si) && si.TryGetInt32(out int siVal))
            socketIndex = siVal;

        bool lockBreakaway = false;
        if (args.Value.TryGetProperty("lockBreakaway", out var lb))
            lockBreakaway = lb.GetBoolean();

        var rule = new RuleEntry
        {
            Id = $"rule-{Guid.NewGuid():N}"[..8],
            Name = name,
            Enabled = true,
            Match = new RuleMatch
            {
                Process = processPattern,
                Path = string.IsNullOrWhiteSpace(pathPattern) ? null : pathPattern
            },
            Action = new RuleAction
            {
                Mode = mode,
                Level = level,
                SocketIndex = socketIndex,
                Lock = lockBreakaway
            }
        };

        _ruleEngine.AddRule(rule);
        SaveRules();
        return new { added = true, ruleId = rule.Id, name = rule.Name };
    }

    private object ExecuteListDrives()
    {
        var roots = OperatingSystem.IsWindows()
            ? Monitoring.DriveService.GetFixedDriveRoots()
            : Monitoring.DriveService.GetAllDriveRoots();
        return new
        {
            count = roots.Count,
            drives = roots,
            crossDriveHint = "Use a path pattern starting with ** to match any drive (e.g. '**\\\\Games\\\\**' or '**\\\\steamapps\\\\common\\\\**')."
        };
    }

    private object ExecuteRemoveRule(JsonElement? args)
    {
        if (args == null) throw new ArgumentException("Missing arguments");

        string ruleId = args.Value.GetProperty("ruleId").GetString()!;
        bool removed = _ruleEngine.RemoveRule(ruleId);
        if (removed) SaveRules();

        return new { removed, ruleId };
    }

    #endregion

    #region Helpers

    private static ulong? ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong val))
            return val;
        return null;
    }

    #endregion
}

#region JSON-RPC Types

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

#endregion
