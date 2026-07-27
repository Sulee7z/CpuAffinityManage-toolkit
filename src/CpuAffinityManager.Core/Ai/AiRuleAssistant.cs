using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using Serilog;

namespace CpuAffinityManager.Ai;

/// <summary>Outcome of an AI rule-generation request.</summary>
public sealed class AiRuleResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<RuleEntry> Rules { get; init; } = new();
    public string Raw { get; init; } = "";
}

/// <summary>
/// Calls a third-party AI provider (any OpenAI-compatible /chat/completions endpoint —
/// OpenAI, DeepSeek, Moonshot/Kimi, Zhipu GLM, OpenRouter, local Ollama, …) using the
/// user's imported API key, and turns a natural-language request into CPU-affinity rules.
/// </summary>
public sealed class AiRuleAssistant
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly ICpuTopologyService _topo;

    public AiRuleAssistant(ICpuTopologyService topo) => _topo = topo;

    public async Task<AiRuleResult> GenerateRulesAsync(AiConfig cfg, string userRequest, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            return new AiRuleResult { Success = false, Error = "尚未填写 API Key" };
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
            return new AiRuleResult { Success = false, Error = "尚未填写 Base URL" };
        if (string.IsNullOrWhiteSpace(userRequest))
            return new AiRuleResult { Success = false, Error = "请先描述你的需求" };

        var topo = _topo.Detect();
        string systemPrompt = BuildSystemPrompt(topo);

        var payload = new
        {
            model = cfg.Model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userRequest }
            }
        };

        string endpoint = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        string raw;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return new AiRuleResult { Success = false, Raw = raw, Error = $"HTTP {(int)resp.StatusCode}:{Truncate(raw, 300)}" };
        }
        catch (Exception ex)
        {
            return new AiRuleResult { Success = false, Error = "请求失败:" + ex.Message };
        }

        // Extract assistant message content.
        string content;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (Exception ex)
        {
            return new AiRuleResult { Success = false, Raw = raw, Error = "无法解析 AI 返回:" + ex.Message };
        }

        var rules = ParseRules(content, out string? parseErr);
        if (rules.Count == 0)
            return new AiRuleResult { Success = false, Raw = content, Error = parseErr ?? "AI 未返回可用规则" };

        return new AiRuleResult { Success = true, Rules = rules, Raw = content };
    }

    private static string BuildSystemPrompt(CpuTopology topo)
    {
        return
            "你是 CPU 亲和性规则生成助手。根据用户需求,只输出一个 JSON 对象,格式为 " +
            "{\"rules\":[ ... ]},不要输出任何解释文字或 Markdown 代码块。\n" +
            "每条规则字段:\n" +
            "  id: 字符串(可用 rule-加简短英文)\n" +
            "  name: 字符串(中文名称)\n" +
            "  enabled: true\n" +
            "  match: { process: 进程名通配符(如 \"*.exe\" 或 \"chrome.exe|msedge.exe\"), " +
            "path: 路径通配符或 null(** 匹配任意盘与层级,如 \"**\\\\Games\\\\**\"), exclude: 字符串数组或 null }\n" +
            "  action: { type: \"cpu-affinity\", mode: 见下, level: 见下, customMask: null, socketIndex: null, " +
            "cpuPriority: null 或 low/belowNormal/normal/aboveNormal/high, lock: false, " +
            "ioPriority: null 或 verylow/low/normal/high, memoryPriority: null 或 1-5, efficiencyMode: false 或 true, " +
            "preferredCores: null 或 十六进制核心掩码字符串(如 \"0x3\"=核心0+1;全核可用但线程持续优先调度到这些核心,不缩小亲和性) }\n" +
            "(后台/省电类进程可用 ioPriority=low、memoryPriority=1、efficiencyMode=true;要“能跑全核但优先某些核”用 mode=all-cores + preferredCores=掩码)\n" +
            "mode 可选:all-cores, p-cores, e-cores, p-cores-smt, p-cores-no-smt, first-half, second-half, custom;" +
            "可用 | 组成回退链(如 \"p-cores|first-half\");可加 @socketN。\n" +
            "level 可选:soft-cpu-sets(软偏好), hard-affinity(进程亲和), job-enforced(Job对象防篡改), job-locked(锁定禁止脱离)。\n" +
            $"本机 CPU:共 {topo.TotalLogicalProcessors} 逻辑处理器,大核 {topo.PcoreCount},小核 {topo.EcoreCount}," +
            $"{(topo.EcoreCount > 0 ? "属于大小核混合架构" : "无独立小核")}。\n" +
            "游戏类建议 p-cores|all-cores + job-enforced + cpuPriority high;后台/更新类建议 e-cores|second-half + soft-cpu-sets。";
    }

    /// <summary>Extracts the rules array from the AI text (tolerates code fences / prose).</summary>
    private static List<RuleEntry> ParseRules(string content, out string? error)
    {
        error = null;
        string json = content.Trim();

        // Strip ``` / ```json fences if present.
        if (json.StartsWith("```"))
        {
            int nl = json.IndexOf('\n');
            if (nl >= 0) json = json[(nl + 1)..];
            int fence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) json = json[..fence];
            json = json.Trim();
        }

        // Reduce to the outermost JSON object/array.
        int objStart = json.IndexOf('{');
        int arrStart = json.IndexOf('[');
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        try
        {
            // Prefer an object with a "rules" property.
            if (objStart >= 0 && (arrStart < 0 || objStart < arrStart))
            {
                int objEnd = json.LastIndexOf('}');
                if (objEnd > objStart)
                {
                    string objJson = json.Substring(objStart, objEnd - objStart + 1);
                    using var doc = JsonDocument.Parse(objJson);
                    if (doc.RootElement.TryGetProperty("rules", out var rulesEl))
                    {
                        var list = JsonSerializer.Deserialize<List<RuleEntry>>(rulesEl.GetRawText(), opts);
                        return list ?? new List<RuleEntry>();
                    }
                    // Single rule object?
                    var single = JsonSerializer.Deserialize<RuleEntry>(objJson, opts);
                    if (single != null && !string.IsNullOrEmpty(single.Match.Process))
                        return new List<RuleEntry> { single };
                }
            }

            // Fall back to a bare array.
            if (arrStart >= 0)
            {
                int arrEnd = json.LastIndexOf(']');
                if (arrEnd > arrStart)
                {
                    string arrJson = json.Substring(arrStart, arrEnd - arrStart + 1);
                    var list = JsonSerializer.Deserialize<List<RuleEntry>>(arrJson, opts);
                    return list ?? new List<RuleEntry>();
                }
            }
        }
        catch (Exception ex)
        {
            error = "解析规则 JSON 失败:" + ex.Message;
            Log.Warning(ex, "AI rule parse failed");
        }

        return new List<RuleEntry>();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
