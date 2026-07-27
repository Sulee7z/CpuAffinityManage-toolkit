using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using CpuAffinityManager.Monitoring;
using Serilog;

namespace CpuAffinityManager.Api;

/// <summary>
/// Lightweight HTTP REST API so third-party AI agents (or any HTTP client) can read
/// the CPU topology / processes and — crucially — CREATE affinity rules programmatically
/// ("让第三方 AI 自动写规则"). Built on the framework's <see cref="HttpListener"/>, so it
/// needs no extra dependencies.
///
/// Endpoints (JSON in/out, all under http://127.0.0.1:&lt;port&gt;):
///   GET  /                       → API manifest (self-describing, for AI discovery)
///   GET  /api/health             → { ok: true }
///   GET  /api/topology           → CPU topology
///   GET  /api/drives             → all fixed drive roots (for path rules across drives)
///   GET  /api/processes?filter=&amp;top=  → running processes
///   GET  /api/rules              → all rules
///   POST /api/rules              → add/update a rule  { name, processPattern, pathPattern?, mode, level, socketIndex?, lockBreakaway?, enabled? }
///   DELETE /api/rules/{id}       → remove a rule
///   POST /api/rules/apply        → apply a rule to a pid  { ruleId, pid }
///   POST /api/affinity           → set affinity on a pid  { pid, mode, level?, customMask?, socketIndex? }
///   POST /api/scan               → scan all processes and enforce matching rules
///
/// Binds to loopback only by default. Rules are persisted to disk after every change.
/// </summary>
public sealed class HttpApiServer
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcement;
    private readonly Action _persist;
    private readonly HttpListener _listener = new();
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public HttpApiServer(
        IRuleEngine ruleEngine,
        ICpuTopologyService topoService,
        IEnforcementService enforcement,
        Action persist,
        int port = 8088,
        bool allowRemote = false)
    {
        _ruleEngine = ruleEngine;
        _topoService = topoService;
        _enforcement = enforcement;
        _persist = persist;
        string host = allowRemote ? "+" : "127.0.0.1";
        _listener.Prefixes.Add($"http://{host}:{port}/");
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _listener.Start();
        Log.Information("HTTP API listening on {Prefixes}", string.Join(", ", _listener.Prefixes));

        using (ct.Register(() => { try { _listener.Stop(); } catch { } }))
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; } // listener stopped

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET,POST,DELETE,OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

        try
        {
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Length == 0) path = "/";
            string method = req.HttpMethod;

            // Root: serve the browser web UI for humans, JSON manifest for programmatic clients.
            if (method == "GET" && (path == "/" || path == "/ui"))
            {
                string accept = req.Headers["Accept"] ?? "";
                if (path == "/ui" || accept.Contains("text/html"))
                {
                    await WriteHtmlAsync(res, 200, WebUiHtml);
                    return;
                }
                await WriteJsonAsync(res, 200, Manifest());
                return;
            }

            object? result;
            int status = 200;

            if (method == "GET" && path == "/api") result = Manifest();
            else if (method == "GET" && path == "/api/health") result = new { ok = true, server = "cpu-affinity-manager", version = "2.4.0" };
            else if (method == "GET" && path == "/api/topology") result = Topology();
            else if (method == "GET" && path == "/api/drives") result = Drives();
            else if (method == "GET" && path == "/api/processes") result = ListProcesses(req);
            else if (method == "GET" && path == "/api/rules") result = GetRules();
            else if (method == "POST" && path == "/api/rules") result = await AddRuleAsync(req);
            else if (method == "DELETE" && path.StartsWith("/api/rules/")) result = RemoveRule(path.Substring("/api/rules/".Length));
            else if (method == "POST" && path == "/api/rules/apply") result = await ApplyRuleAsync(req);
            else if (method == "POST" && path == "/api/affinity") result = await SetAffinityAsync(req);
            else if (method == "POST" && path == "/api/scan") result = new { affectedProcesses = _enforcement.ScanAndEnforce() };
            else { status = 404; result = new { error = "not found", path }; }

            await WriteJsonAsync(res, status, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP API error");
            try { await WriteJsonAsync(res, 400, new { error = ex.Message }); } catch { }
        }
    }

    // ── Handlers ──

    private object Manifest() => new
    {
        name = "CPU Affinity Manager HTTP API",
        version = "2.4.0",
        webUi = "在浏览器打开本地址即为网页控制台;编程调用返回本 JSON 清单。",
        description = "Read CPU topology/processes and create CPU-affinity rules programmatically. Intended for third-party AI agents.",
        endpoints = new object[]
        {
            new { method = "GET", path = "/api/topology", desc = "CPU topology (P/E cores, sockets, masks)" },
            new { method = "GET", path = "/api/drives", desc = "Fixed drive roots for cross-drive path rules" },
            new { method = "GET", path = "/api/processes?filter=&top=", desc = "Running processes" },
            new { method = "GET", path = "/api/rules", desc = "List rules" },
            new { method = "POST", path = "/api/rules", desc = "Add/update a rule", body = new { name = "string", processPattern = "e.g. game*.exe", pathPattern = "optional, e.g. **\\\\steamapps\\\\common\\\\**", mode = "p-cores|first-half", level = "job-enforced", socketIndex = "int?", lockBreakaway = "bool?", enabled = "bool?" } },
            new { method = "DELETE", path = "/api/rules/{id}", desc = "Remove a rule" },
            new { method = "POST", path = "/api/rules/apply", desc = "Apply a rule to a pid", body = new { ruleId = "string", pid = "int" } },
            new { method = "POST", path = "/api/affinity", desc = "Set affinity on a pid", body = new { pid = "int", mode = "string", level = "string?", customMask = "hex?", socketIndex = "int?" } },
            new { method = "POST", path = "/api/scan", desc = "Scan all processes and enforce matching rules" }
        },
        modes = ICpuTopologyService.AvailableModes,
        levels = new[] { "soft-cpu-sets", "hard-affinity", "job-enforced", "job-locked" }
    };

    private object Topology()
    {
        var t = _topoService.Detect();
        return new
        {
            totalLogicalProcessors = t.TotalLogicalProcessors,
            pCoreCount = t.PcoreCount,
            eCoreCount = t.EcoreCount,
            smtEnabled = t.SmtEnabled,
            socketCount = t.SocketCount,
            pCoreMask = $"0x{t.PcoreMask:X}",
            eCoreMask = $"0x{t.EcoreMask:X}",
            availableModes = ICpuTopologyService.AvailableModes
        };
    }

    private object Drives()
    {
        var roots = OperatingSystem.IsWindows()
            ? DriveService.GetFixedDriveRoots()
            : DriveService.GetAllDriveRoots();
        return new { count = roots.Count, drives = roots, crossDriveHint = "Use a path pattern starting with ** to match any drive, e.g. **\\Games\\**" };
    }

    private object ListProcesses(HttpListenerRequest req)
    {
        string? filter = req.QueryString["filter"];
        int top = int.TryParse(req.QueryString["top"], out int t) ? Math.Min(t, 200) : 50;

        var list = new List<object>();
        foreach (var p in Process.GetProcesses())
        {
            if (list.Count >= top) break;
            try
            {
                string name = p.ProcessName + ".exe";
                if (!string.IsNullOrEmpty(filter) && !Wildcard.Match(name, filter, true)) continue;
                int pid = p.Id;
                string? path = pid is 0 or 4 ? null : EnforcementService.GetProcessPath(pid);
                list.Add(new
                {
                    pid,
                    name,
                    path = path ?? "(protected)",
                    affinity = $"0x{p.ProcessorAffinity.ToInt64():X}"
                });
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return new { count = list.Count, processes = list };
    }

    private object GetRules()
    {
        var rules = _ruleEngine.Rules.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            enabled = r.Enabled,
            processPattern = r.Match.Process,
            pathPattern = r.Match.Path,
            exclude = r.Match.Exclude,
            mode = r.Action.Mode,
            level = r.Action.Level,
            socketIndex = r.Action.SocketIndex,
            lockBreakaway = r.Action.Lock,
            cpuPriority = r.Action.CpuPriority
        }).ToList();
        return new { count = rules.Count, rules };
    }

    private async Task<object> AddRuleAsync(HttpListenerRequest req)
    {
        var body = await ReadJsonAsync(req);
        string name = GetString(body, "name") ?? throw new ArgumentException("name is required");
        string processPattern = GetString(body, "processPattern") ?? throw new ArgumentException("processPattern is required");
        string mode = GetString(body, "mode") ?? throw new ArgumentException("mode is required");
        string level = GetString(body, "level") ?? "hard-affinity";
        string? pathPattern = GetString(body, "pathPattern");
        string? id = GetString(body, "id");
        string? cpuPriority = GetString(body, "cpuPriority");
        int? socketIndex = GetInt(body, "socketIndex");
        bool lockBreakaway = GetBool(body, "lockBreakaway") ?? false;
        bool enabled = GetBool(body, "enabled") ?? true;

        var rule = new RuleEntry
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"rule-{Guid.NewGuid():N}"[..8] : id!,
            Name = name,
            Enabled = enabled,
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
                Lock = lockBreakaway,
                CpuPriority = cpuPriority
            }
        };

        _ruleEngine.AddRule(rule);
        _persist();
        return new { added = true, ruleId = rule.Id, name = rule.Name };
    }

    private object RemoveRule(string id)
    {
        bool removed = _ruleEngine.RemoveRule(id);
        if (removed) _persist();
        return new { removed, ruleId = id };
    }

    private async Task<object> ApplyRuleAsync(HttpListenerRequest req)
    {
        var body = await ReadJsonAsync(req);
        string ruleId = GetString(body, "ruleId") ?? throw new ArgumentException("ruleId is required");
        int pid = GetInt(body, "pid") ?? throw new ArgumentException("pid is required");

        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == ruleId)
                   ?? throw new InvalidOperationException($"Rule '{ruleId}' not found");
        bool ok = _enforcement.Apply(pid, rule, _topoService.Detect());
        return new { success = ok, ruleId, pid, ruleName = rule.Name };
    }

    private async Task<object> SetAffinityAsync(HttpListenerRequest req)
    {
        var body = await ReadJsonAsync(req);
        int pid = GetInt(body, "pid") ?? throw new ArgumentException("pid is required");
        string mode = GetString(body, "mode") ?? throw new ArgumentException("mode is required");
        string level = GetString(body, "level") ?? "hard-affinity";
        string? customMask = GetString(body, "customMask");
        int? socketIndex = GetInt(body, "socketIndex");

        var rule = new RuleEntry
        {
            Id = "adhoc",
            Name = "Ad-hoc HTTP call",
            Action = new RuleAction { Mode = mode, Level = level, CustomMask = customMask, SocketIndex = socketIndex }
        };
        bool ok = _enforcement.Apply(pid, rule, _topoService.Detect());
        return new { success = ok, pid, mode, level };
    }

    // ── JSON helpers ──

    private static async Task<JsonElement> ReadJsonAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        string raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw)) return default;
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static bool? GetBool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    private async Task WriteJsonAsync(HttpListenerResponse res, int status, object? payload)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse res, int status, string html)
    {
        res.StatusCode = status;
        res.ContentType = "text/html; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    // ── Browser web UI (self-contained; follows the system light/dark theme) ──
    private const string WebUiHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CPU 亲和性管理器 · 网页控制台</title>
<style>
  :root{
    --accent:#005FB8; --bg:#f3f3f3; --card:#fdfdfd; --card2:#f5f5f5;
    --text:#1b1b1b; --muted:#5d5d5d; --border:#e5e5e5; --danger:#c42b1c;
    --radius:8px;
  }
  @media (prefers-color-scheme: dark){
    :root{ --accent:#60cdff; --bg:#202020; --card:#2b2b2b; --card2:#323232;
      --text:#ffffff; --muted:#cfcfcf; --border:#3a3a3a; --danger:#ff99a4; }
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);
    font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;font-size:14px}
  header{padding:20px 28px;display:flex;align-items:center;gap:12px;
    border-bottom:1px solid var(--border)}
  header .dot{width:12px;height:12px;border-radius:50%;background:var(--accent)}
  header h1{font-size:18px;margin:0;font-weight:600}
  header .ver{margin-left:auto;color:var(--muted);font-size:12px}
  main{max-width:1100px;margin:0 auto;padding:20px 28px;display:grid;gap:16px}
  .card{background:var(--card);border:1px solid var(--border);border-radius:var(--radius);
    padding:18px 20px;box-shadow:0 1px 3px rgba(0,0,0,.08)}
  .card h2{margin:0 0 12px;font-size:15px;font-weight:600}
  .stats{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}
  .stat{background:var(--card2);border-radius:6px;padding:14px}
  .stat .n{font-size:26px;font-weight:700}
  .stat .l{font-size:12px;color:var(--muted)}
  .row{display:flex;flex-wrap:wrap;gap:10px;align-items:center}
  label{font-size:12px;color:var(--muted);display:block;margin-bottom:4px}
  input,select{background:var(--card);color:var(--text);border:1px solid var(--border);
    border-radius:4px;padding:8px 10px;font-size:14px;min-height:34px}
  input:focus,select:focus{outline:none;border-color:var(--accent)}
  button{background:var(--accent);color:#fff;border:0;border-radius:4px;padding:9px 16px;
    font-size:14px;font-weight:600;cursor:pointer;min-height:34px}
  button.sec{background:transparent;color:var(--text);border:1px solid var(--border)}
  button.del{background:transparent;color:var(--danger);padding:6px 10px;font-weight:500}
  @media (prefers-color-scheme: dark){ button{color:#000} }
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--border)}
  th{color:var(--muted);font-weight:600}
  .tag{display:inline-block;background:var(--card2);border-radius:4px;padding:2px 8px;font-size:12px}
  .muted{color:var(--muted)}
  .grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
  .search{flex:1;min-width:200px}
</style>
</head>
<body>
<header>
  <span class="dot"></span>
  <h1>CPU 亲和性管理器 · 网页控制台</h1>
  <span class="ver" id="ver"></span>
</header>
<main>
  <section class="card">
    <h2>CPU 拓扑</h2>
    <div class="stats" id="topo"><div class="muted">加载中…</div></div>
  </section>

  <section class="card">
    <h2>新建规则（第三方 AI 也可通过 POST /api/rules 自动写入）</h2>
    <div class="grid2">
      <div><label>规则名称</label><input id="r_name" placeholder="游戏绑大核"></div>
      <div><label>进程名通配符</label><input id="r_proc" placeholder="*.exe"></div>
      <div><label>路径通配符（** 匹配所有盘,可留空）</label><input id="r_path" placeholder="**\Games\**"></div>
      <div class="row">
        <div style="flex:1"><label>模式</label>
          <select id="r_mode" style="width:100%"></select></div>
        <div style="flex:1"><label>级别</label>
          <select id="r_level" style="width:100%"></select></div>
      </div>
    </div>
    <div class="row" style="margin-top:12px">
      <button onclick="addRule()">添加规则</button>
      <button class="sec" onclick="scan()">扫描并强制应用</button>
      <span id="msg" class="muted"></span>
    </div>
  </section>

  <section class="card">
    <h2>规则</h2>
    <table><thead><tr><th>名称</th><th>进程</th><th>路径</th><th>模式</th><th>级别</th><th></th></tr></thead>
      <tbody id="rules"></tbody></table>
  </section>

  <section class="card">
    <div class="row" style="margin-bottom:10px">
      <h2 style="margin:0">进程</h2>
      <input class="search" id="filter" placeholder="按名称筛选,如 chrome*" oninput="loadProcs()">
    </div>
    <table><thead><tr><th>PID</th><th>名称</th><th>路径</th><th>亲和性</th></tr></thead>
      <tbody id="procs"></tbody></table>
  </section>
</main>
<script>
const api = (p,o)=>fetch(p,o).then(r=>r.json());
const el = id=>document.getElementById(id);
const esc = s=>String(s??"").replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));

async function init(){
  const m = await api('/api'); el('ver').textContent='v'+m.version;
  el('r_mode').innerHTML = m.modes.map(x=>`<option>${x}</option>`).join('');
  el('r_level').innerHTML = m.levels.map(x=>`<option>${x}</option>`).join('');
  loadTopo(); loadRules(); loadProcs();
}
async function loadTopo(){
  const t = await api('/api/topology');
  el('topo').innerHTML =
    stat(t.totalLogicalProcessors,'逻辑处理器')+stat(t.pCoreCount,'大核 P')+
    stat(t.eCoreCount,'小核 E')+stat(t.socketCount,'插槽');
}
const stat=(n,l)=>`<div class="stat"><div class="n">${n}</div><div class="l">${l}</div></div>`;
async function loadRules(){
  const d = await api('/api/rules');
  el('rules').innerHTML = (d.rules||[]).map(r=>`<tr>
    <td>${esc(r.name)}</td><td><span class="tag">${esc(r.processPattern)}</span></td>
    <td class="muted">${esc(r.pathPattern||'')}</td>
    <td><span class="tag">${esc(r.mode)}</span></td><td><span class="tag">${esc(r.level)}</span></td>
    <td><button class="del" onclick="delRule('${esc(r.id)}')">删除</button></td></tr>`).join('')
    || '<tr><td colspan="6" class="muted">暂无规则</td></tr>';
}
async function loadProcs(){
  const f = el('filter').value.trim();
  const d = await api('/api/processes?top=100'+(f?'&filter='+encodeURIComponent(f):''));
  el('procs').innerHTML = (d.processes||[]).map(p=>`<tr>
    <td>${p.pid}</td><td>${esc(p.name)}</td><td class="muted">${esc(p.path)}</td>
    <td class="muted">${esc(p.affinity)}</td></tr>`).join('')
    || '<tr><td colspan="4" class="muted">无</td></tr>';
}
async function addRule(){
  const body={name:el('r_name').value.trim(),processPattern:el('r_proc').value.trim(),
    pathPattern:el('r_path').value.trim(),mode:el('r_mode').value,level:el('r_level').value};
  if(!body.name||!body.processPattern){msg('请填写名称和进程名');return;}
  const r=await api('/api/rules',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  msg(r.added?('已添加:'+r.ruleId):('失败:'+(r.error||'')));
  el('r_name').value='';el('r_proc').value='';el('r_path').value=''; loadRules();
}
async function delRule(id){ await api('/api/rules/'+id,{method:'DELETE'}); loadRules(); }
async function scan(){ const r=await api('/api/scan',{method:'POST'}); msg('扫描完成,影响 '+r.affectedProcesses+' 个进程'); loadProcs(); }
function msg(t){ el('msg').textContent=t; setTimeout(()=>el('msg').textContent='',4000); }
init();
</script>
</body>
</html>
""";
}
