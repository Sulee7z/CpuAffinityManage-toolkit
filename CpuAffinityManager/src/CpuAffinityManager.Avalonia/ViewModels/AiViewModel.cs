using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.Ai;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using Serilog;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class AiViewModel : ViewModelBase
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topo;
    private readonly Action _persistAndRefresh;
    private readonly AiRuleAssistant _assistant;

    // provider preset → (baseUrl, model)
    private static readonly (string Name, string BaseUrl, string Model)[] Presets =
    {
        ("OpenAI",            "https://api.openai.com/v1",        "gpt-4o-mini"),
        ("DeepSeek",          "https://api.deepseek.com/v1",      "deepseek-chat"),
        ("Kimi (Moonshot)",   "https://api.moonshot.cn/v1",       "moonshot-v1-8k"),
        ("智谱 GLM",          "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("OpenRouter",        "https://openrouter.ai/api/v1",     "openai/gpt-4o-mini"),
        ("Ollama (本地)",     "http://localhost:11434/v1",        "qwen2.5"),
        ("自定义",            "",                                 ""),
    };

    public static string[] ProviderNames { get; } = Array.ConvertAll(Presets, p => p.Name);

    [ObservableProperty] private int _selectedProviderIndex;
    [ObservableProperty] private string _baseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _showApiKey;
    [ObservableProperty] private string _model = "gpt-4o-mini";
    [ObservableProperty] private string _requestText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _preview = "";

    public AiViewModel(IRuleEngine ruleEngine, ICpuTopologyService topo, Action persistAndRefresh)
    {
        _ruleEngine = ruleEngine;
        _topo = topo;
        _persistAndRefresh = persistAndRefresh;
        _assistant = new AiRuleAssistant(topo);

        var cfg = AiConfig.Load();
        BaseUrl = cfg.BaseUrl;
        ApiKey = cfg.ApiKey;
        Model = cfg.Model;
        int idx = Array.FindIndex(Presets, p => p.Name == cfg.Provider);
        _selectedProviderIndex = idx >= 0 ? idx : Presets.Length - 1; // default 自定义
        if (!string.IsNullOrEmpty(cfg.ApiKey))
            Status = "已载入保存的配置";
    }

    partial void OnSelectedProviderIndexChanged(int value)
    {
        if (value < 0 || value >= Presets.Length) return;
        var p = Presets[value];
        if (p.Name == "自定义") return;         // don't clobber custom entries
        BaseUrl = p.BaseUrl;
        Model = p.Model;
    }

    [RelayCommand]
    private void SaveConfig()
    {
        var cfg = new AiConfig
        {
            Provider = Presets[Math.Clamp(SelectedProviderIndex, 0, Presets.Length - 1)].Name,
            BaseUrl = BaseUrl.Trim(),
            ApiKey = ApiKey.Trim(),
            Model = Model.Trim()
        };
        cfg.Save();
        Status = "配置已保存(API Key 保存在本机用户目录)";
    }

    [RelayCommand]
    private async Task GenerateRulesAsync()
    {
        await GenerateInternal(async (cfg, ct) => await _assistant.GenerateRulesAsync(cfg, RequestText.Trim(), ct));
    }

    [RelayCommand]
    private async Task AutoGenerateAsync()
    {
        RequestText = "自动检测环境并生成最优规则…";
        Status = "正在扫描系统环境和游戏…";
        await GenerateInternal(async (cfg, ct) => await _assistant.AutoGenerateAsync(cfg, ct));
    }

    private async Task GenerateInternal(Func<AiConfig, System.Threading.CancellationToken, Task<AiRuleResult>> generator)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "正在请求 AI…";
        Preview = "";
        try
        {
            var cfg = new AiConfig
            {
                Provider = Presets[Math.Clamp(SelectedProviderIndex, 0, Presets.Length - 1)].Name,
                BaseUrl = BaseUrl.Trim(),
                ApiKey = ApiKey.Trim(),
                Model = Model.Trim()
            };
            cfg.Save();

            var result = await generator(cfg, default);
            if (!result.Success)
            {
                Status = "失败:" + result.Error;
                Preview = result.Raw;
                return;
            }

            int added = 0;
            foreach (var r in result.Rules)
            {
                if (string.IsNullOrWhiteSpace(r.Id))
                    r.Id = "ai-" + Guid.NewGuid().ToString("N")[..6];
                if (r.Action == null || string.IsNullOrWhiteSpace(r.Match?.Process))
                    continue;
                _ruleEngine.AddRule(r);
                added++;
            }

            _persistAndRefresh();
            Status = $"已生成并导入 {added} 条规则";
            Preview = string.Join("\n", System.Linq.Enumerable.Select(result.Rules,
                r => $"• {r.Name}  →  {r.Action?.Mode} [{r.Action?.Level}]  匹配 {r.Match?.Process}"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI generate failed");
            Status = "出错:" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}