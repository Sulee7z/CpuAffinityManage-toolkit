using System;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.Avalonia.Services;
using CpuAffinityManager.ProcOps;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ApiServerService? _api;

    [ObservableProperty] private bool _enableWmiMonitor = true;
    [ObservableProperty] private bool _confirmBeforeApply;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private int _selectedThemeIndex;
    [ObservableProperty] private string _appVersion = "v2.20.0";

    // true = 指定核心优先级高(手动选择的核心压过规则);false = 规则优先级高。
    [ObservableProperty] private bool _manualCoreWins = ManualAffinityRegistry.ManualWins;

    // ── Third-party AI HTTP API ──
    [ObservableProperty] private bool _apiRunning;
    [ObservableProperty] private string _apiPort = "8088";
    [ObservableProperty] private bool _apiAllowRemote;
    [ObservableProperty] private string _apiStatus = "未启动";
    [ObservableProperty] private string _apiUrl = "http://127.0.0.1:8088";

    public static string[] ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    public SettingsViewModel() { }

    public SettingsViewModel(ApiServerService api)
    {
        _api = api;
        if (OperatingSystem.IsWindows())
            _runAtStartup = StartupService.IsSelfStartupEnabled(); // set field, don't trigger apply
    }

    /// <summary>Registers/removes this app from user-logon auto-start.</summary>
    partial void OnRunAtStartupChanged(bool value)
    {
        if (OperatingSystem.IsWindows())
            StartupService.SetSelfStartup(value);
    }

    /// <summary>切换“指定核心优先级高 / 规则优先级高”。</summary>
    partial void OnManualCoreWinsChanged(bool value) => ManualAffinityRegistry.ManualWins = value;

    /// <summary>
    /// Applies the selected theme to the whole application.
    /// 0 = 跟随系统 (Default) · 1 = 浅色 (Light) · 2 = 深色 (Dark).
    /// </summary>
    partial void OnSelectedThemeIndexChanged(int value)
    {
        var variant = value switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = variant;
    }

    /// <summary>Example request third-party AI agents can copy to auto-write rules.</summary>
    public string ApiExample =>
        "# 让第三方 AI 自动写规则(PowerShell 示例)\n" +
        "curl -Method POST " + ApiUrl + "/api/rules `\n" +
        "  -ContentType 'application/json' `\n" +
        "  -Body '{\"name\":\"游戏绑大核\",\"processPattern\":\"*.exe\",\"pathPattern\":\"**\\\\Games\\\\**\",\"mode\":\"p-cores|all-cores\",\"level\":\"job-enforced\"}'\n\n" +
        "# 读取接口清单(供 AI 自我发现):GET " + ApiUrl + "/";

    [RelayCommand]
    private void ToggleApi()
    {
        if (_api == null) return;

        if (ApiRunning)
        {
            _api.Stop();
            ApiRunning = false;
            ApiStatus = "未启动";
            return;
        }

        if (!int.TryParse(ApiPort, out int port) || port < 1 || port > 65535)
        {
            ApiStatus = "端口号无效(应为 1–65535)";
            return;
        }

        string? err = _api.Start(port, ApiAllowRemote);
        if (err == null)
        {
            ApiRunning = true;
            ApiUrl = _api.Url;
            ApiStatus = "运行中 · " + _api.Url;
            OnPropertyChanged(nameof(ApiExample));
        }
        else
        {
            ApiRunning = false;
            ApiStatus = "启动失败:" + err + "(可尝试以管理员身份运行)";
        }
    }
}
