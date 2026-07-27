<<<<<<< HEAD
using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
=======
using System.Net.Http;
using System.Net.Http.Headers;
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
using System.Text;
using System.Text.Json;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using Serilog;

namespace CpuAffinityManager.Ai;

<<<<<<< HEAD
=======
/// <summary>Outcome of an AI rule-generation request.</summary>
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
public sealed class AiRuleResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<RuleEntry> Rules { get; init; } = new();
    public string Raw { get; init; } = "";
}

<<<<<<< HEAD
public sealed class AiRuleAssistant
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
=======
/// <summary>
/// Calls a third-party AI provider (any OpenAI-compatible /chat/completions endpoint —
/// OpenAI, DeepSeek, Moonshot/Kimi, Zhipu GLM, OpenRouter, local Ollama, …) using the
/// user's imported API key, and turns a natural-language request into CPU-affinity rules.
/// </summary>
public sealed class AiRuleAssistant
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    private readonly ICpuTopologyService _topo;

    public AiRuleAssistant(ICpuTopologyService topo) => _topo = topo;

    public async Task<AiRuleResult> GenerateRulesAsync(AiConfig cfg, string userRequest, CancellationToken ct = default)
    {
<<<<<<< HEAD
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) return Fail("尚未填写 API Key");
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) return Fail("尚未填写 Base URL");
        if (string.IsNullOrWhiteSpace(userRequest)) return Fail("请先描述你的需求");

        var topo = _topo.Detect();
        string prompt = BuildPrompt(topo, userRequest, detectGames: true);

        var payload = new
        {
            model = cfg.Model, temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = prompt },
=======
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
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
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
<<<<<<< HEAD
            using var resp = await Http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new AiRuleResult { Success = false, Raw = raw, Error = $"HTTP {(int)resp.StatusCode}:{Trunc(raw, 300)}" };
        }
        catch (Exception ex) { return Fail("请求失败:" + ex.Message); }

=======

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
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
        string content;
        try
        {
            using var doc = JsonDocument.Parse(raw);
<<<<<<< HEAD
            content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex) { return new AiRuleResult { Success = false, Raw = raw, Error = "解析失败:" + ex.Message }; }

        var rules = ParseRules(content, out string? pe);
        return rules.Count == 0
            ? new AiRuleResult { Success = false, Raw = content, Error = pe ?? "AI 未返回规则" }
            : new AiRuleResult { Success = true, Rules = rules, Raw = content };
    }

    public async Task<AiRuleResult> AutoGenerateAsync(AiConfig cfg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) return Fail("尚未填写 API Key");
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) return Fail("尚未填写 Base URL");

        var topo = _topo.Detect();
        string sysInfo = DetectFullSystemInfo(topo);
        string games = DetectAllGames();

        string request = $"根据以下系统环境,只为我实际检测到的游戏/应用生成规则,不要编造不存在的游戏:\n\n{sysInfo}";
        if (!string.IsNullOrWhiteSpace(games))
            request += $"\n\n已检测到的游戏和应用:\n{games}";
        request += "\n\n只为我已检测到的应用生成专属亲和性规则。游戏绑定大核+防篡改,后台/更新放小核+省电。";
        return await GenerateRulesAsync(cfg, request, ct);
    }

    private static string DetectFullSystemInfo(CpuTopology topo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CPU: {topo.TotalLogicalProcessors}逻辑处理器 P:{topo.PcoreCount} E:{topo.EcoreCount} SMT:{(topo.SmtEnabled?"开":"关")}");
        sb.Append($"P核掩码:0x{topo.PcoreMask:X} E核掩码:0x{topo.EcoreMask:X}");

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor");
            foreach (var o in searcher.Get())
                sb.AppendLine($"\nCPU型号: {o["Name"]} {o["NumberOfCores"]}核{o["NumberOfLogicalProcessors"]}线程 {Convert.ToInt32(o["MaxClockSpeed"]??0)/1000}MHz");
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,Speed,Caption FROM Win32_PhysicalMemory");
            long ram = 0; string ramType = "";
            foreach (var o in searcher.Get())
            {
                ram += Convert.ToInt64(o["TotalVisibleMemorySize"] ?? 0);
                ramType = o["Caption"]?.ToString() ?? "";
            }
            sb.AppendLine($"内存: {ram / (1024 * 1024)}GB {ramType}");
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber,OSArchitecture FROM Win32_OperatingSystem");
            foreach (var o in searcher.Get())
                sb.AppendLine($"系统: {o["Caption"]} {o["Version"]} Build{o["BuildNumber"]} {o["OSArchitecture"]}");
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name,AdapterRAM,DriverVersion,VideoProcessor FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                long vram = Convert.ToInt64(o["AdapterRAM"] ?? 0L) / (1024 * 1024 * 1024);
                string gpuName = o["Name"]?.ToString() ?? "";
                sb.AppendLine($"显卡: {gpuName} VRAM:{vram}GB 驱动:{o["DriverVersion"]}");
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name,Size,Model,MediaType FROM Win32_DiskDrive");
            foreach (var o in searcher.Get())
            {
                long size = Convert.ToInt64(o["Size"] ?? 0L) / (1024L * 1024 * 1024 * 1024);
                sb.AppendLine($"磁盘: {o["Model"]} {size}TB ({o["MediaType"]})");
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PowerPlan WHERE IsActive=True");
            foreach (var o in searcher.Get())
                sb.AppendLine($"电源计划: {o["Name"]}");
        }
        catch { }

        sb.AppendLine($"环境变量: PROCESSOR_ARCHITECTURE={Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")} NUMBER_OF_PROCESSORS={Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS")}");
        return sb.ToString();
    }

    private static string DetectAllGames()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<(string name, string path, string process)>();

        string[] knownExes =
        {
            "cs2.exe","valorant.exe","R5Apex.exe","r5apex.exe","ModernWarfare.exe","cod.exe",
            "TslGame.exe","TslGame_BE.exe","FortniteClient-Win64-Shipping.exe","RainbowSix.exe",
            "RainbowSix_BE.exe","bfv.exe","bf2042.exe","Cyberpunk2077.exe","eldenring.exe",
            "GTA5.exe","gta5.exe","RDR2.exe","PlayRDR2.exe","witcher3.exe","DOTA2.exe",
            "dota2.exe","Overwatch.exe","MarvelRivals.exe","DeltaForceClient-Win64-Shipping.exe",
            "warthunder.exe","aces.exe","Minecraft.exe","javaw.exe","RobloxPlayerBeta.exe",
            "GenshinImpact.exe","YuanShen.exe","HonkaiImpact3rd.exe","StarRail.exe",
            "WutheringWaves.exe","ZenlessZoneZero.exe","PathOfExile.exe","PathOfExile_x64.exe",
            "Diablo IV.exe","Wow.exe","WowClassic.exe","ffxiv.exe","ffxiv_dx11.exe",
        };

        string[] libPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam","steamapps","common"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "World of Warcraft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"XboxGames"),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string root = drive.RootDirectory.FullName;
            var steamLib = Path.Combine(root, "SteamLibrary", "steamapps", "common");
            if (Directory.Exists(steamLib)) libPaths = [..libPaths, steamLib];
            var steamDef = Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common");
            if (Directory.Exists(steamDef)) libPaths = [..libPaths, steamDef];
        }

        foreach (string basePath in libPaths)
        {
            if (!Directory.Exists(basePath)) continue;
            try
            {
                foreach (string dir in Directory.GetDirectories(basePath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (found.Contains(dirName)) continue;
                    try
                    {
                        var exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
                            .Where(f => !f.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)
                                     && !f.Contains("CrashReport", StringComparison.OrdinalIgnoreCase)
                                     && !f.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                            .Select(Path.GetFileName)
                            .Take(5).ToList();
                        if (exes.Count > 0)
                        {
                            found.Add(dirName);
                            games.Add((dirName, basePath, string.Join(", ", exes)));
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        if (games.Count == 0)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(drive.RootDirectory.FullName, "*.exe", SearchOption.AllDirectories))
                    {
                        string fn = Path.GetFileName(f);
                        if (knownExes.Any(k => fn.Equals(k, StringComparison.OrdinalIgnoreCase)))
                        {
                            string parent = Path.GetFileName(Path.GetDirectoryName(f)) ?? fn;
                            if (found.Add(parent))
                                games.Add((parent, Path.GetDirectoryName(f) ?? "", fn));
                        }
                        if (games.Count >= 40) break;
                    }
                }
                catch { }
                if (games.Count >= 40) break;
            }
        }

        if (games.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var (name, _, proc) in games)
            sb.AppendLine($"  {name}  ({proc})");
        return sb.ToString();
    }

    private static string BuildPrompt(CpuTopology topo, string userRequest, bool detectGames)
    {
        string sysInfo = DetectFullSystemInfo(topo);
        string games = (detectGames && (userRequest.Contains("自动") || userRequest.Contains("生成") || userRequest.Contains("推荐")))
            ? DetectAllGames() : "";

        var sb = new StringBuilder();
        sb.AppendLine("你是CPU亲和性规则生成器。只输出JSON:{\"rules\":[...]},无其他文字。");
        sb.AppendLine();
        sb.AppendLine("规则字段(id/name/enabled/match/action),action字段:type(\"cpu-affinity\"),mode,level,customMask(null),socketIndex(null),");
        sb.AppendLine("  cpuPriority(null/low/belowNormal/normal/aboveNormal/high/realtime),lock(false),");
        sb.AppendLine("  ioPriority(null/verylow/low/normal/high),memoryPriority(null/1-5),efficiencyMode(false/true),");
        sb.AppendLine("  gpuPriority(null/0-5),preferredCores(null/十六进制位掩码),schedulingPool(null/十六进制),preferMode(null/dynamic/static/d2/d3)");
        sb.AppendLine("mode:all-cores/p-cores/e-cores/p-cores-smt/p-cores-no-smt/first-half/second-half/custom(支持|回退链+@socketN)");
        sb.AppendLine("level:soft-cpu-sets/hard-affinity/job-enforced/job-locked");
        sb.AppendLine($"\n=== 系统环境 ===");
        sb.AppendLine(sysInfo);
        if (!string.IsNullOrWhiteSpace(games))
            sb.AppendLine($"\n=== 已检测应用 ===");
            sb.AppendLine(games);
            sb.AppendLine("只为我已检测到的应用生成规则,不要编造。");
        sb.AppendLine("\n策略:3A/竞技→p-cores+job-enforced+cpuPriority=high+gpuPriority=4+preferMode=dynamic+preferredCores=大核掩码");
        sb.Append("轻量→all-cores+soft-cpu-sets+gpuPriority=2; 后台/更新→e-cores+soft-cpu-sets+efficiencyMode+ioPriority=low+gpuPriority=0");
        return sb.ToString();
    }

=======
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
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    private static List<RuleEntry> ParseRules(string content, out string? error)
    {
        error = null;
        string json = content.Trim();
<<<<<<< HEAD
        if (json.StartsWith("```")) { int nl = json.IndexOf('\n'); if (nl >= 0) json = json[(nl + 1)..]; int f = json.LastIndexOf("```"); if (f >= 0) json = json[..f]; json = json.Trim(); }
        int os = json.IndexOf('{'), as_ = json.IndexOf('[');
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            if (os >= 0 && (as_ < 0 || os < as_))
            {
                int oe = json.LastIndexOf('}');
                if (oe > os) { string ojson = json.Substring(os, oe - os + 1); using var d = JsonDocument.Parse(ojson);
                    if (d.RootElement.TryGetProperty("rules", out var re))
                        return JsonSerializer.Deserialize<List<RuleEntry>>(re.GetRawText(), opts) ?? [];
                    var s = JsonSerializer.Deserialize<RuleEntry>(ojson, opts);
                    if (s != null && !string.IsNullOrEmpty(s.Match.Process)) return [s]; }
            }
            if (as_ >= 0) { int ae = json.LastIndexOf(']'); if (ae > as_) { string ajson = json.Substring(as_, ae - as_ + 1); return JsonSerializer.Deserialize<List<RuleEntry>>(ajson, opts) ?? []; } }
        }
        catch (Exception ex) { error = "JSON解析:" + ex.Message; Log.Warning(ex, "AI parse"); }
        return [];
    }

    private static AiRuleResult Fail(string msg) => new() { Success = false, Error = msg };
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
=======

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
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
}
