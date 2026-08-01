# =====================================================================
#  CPU Affinity Manager — 一键编译脚本
#  自动检测 .NET 10 SDK → 编译 GUI / WPF / MCP → 输出到「成品」文件夹
# =====================================================================
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "CpuAffinityManager"
$out  = Join-Path $root "成品"

Write-Host ""
Write-Host "=== CPU Affinity Manager 一键编译 ===" -ForegroundColor Cyan
Write-Host ""

# ---------- 1. 检查 .NET 10 SDK ----------
function Test-Sdk10 {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        return ($sdks | Where-Object { $_ -match "^1[0-9]\." }).Count -gt 0
    } catch { return $false }
}

if (-not (Test-Sdk10)) {
    Write-Host "未检测到 .NET 10 SDK，尝试通过 winget 自动安装..." -ForegroundColor Yellow
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        & winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        # 刷新 PATH
        $env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
                    [Environment]::GetEnvironmentVariable("Path","User")
    }
    if (-not (Test-Sdk10)) {
        Write-Host ""
        Write-Host "自动安装失败。请手动安装 .NET 10 SDK 后重新运行本脚本：" -ForegroundColor Red
        Write-Host "  https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0" -ForegroundColor Red
        Start-Process "https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0"
        exit 1
    }
}
Write-Host "[OK] .NET SDK 就绪：$((dotnet --version))" -ForegroundColor Green

# ---------- 2. 编译三个组件 ----------
$publishArgs = @(
    "-c", "Release", "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishAot=false",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

$targets = @(
    @{ Name = "图形界面 (Avalonia)"; Proj = "src\CpuAffinityManager.Avalonia"; Out = "GUI"     },
    @{ Name = "图形界面 (WPF)";      Proj = "src\CpuAffinityManager.App";      Out = "GUI-WPF" },
    @{ Name = "MCP Server (AI接口)"; Proj = "src\CpuAffinityManager.Mcp";      Out = "MCP"     }
)

foreach ($t in $targets) {
    Write-Host ""
    Write-Host ">>> 正在编译：$($t.Name) ..." -ForegroundColor Cyan
    $dest = Join-Path $out $t.Out
    & dotnet publish (Join-Path $src $t.Proj) @publishArgs -o $dest
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[失败] $($t.Name) 编译出错（见上方错误信息）" -ForegroundColor Red
        exit 1
    }
    # 每个组件旁边放一份规则配置
    $cfgDir = Join-Path $dest "config"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    Copy-Item (Join-Path $src "config\default-rules.json") $cfgDir -Force
    Write-Host "[OK] $($t.Name) → 成品\$($t.Out)" -ForegroundColor Green
}

# ---------- 3. 附带文档 ----------
Copy-Item (Join-Path $src "README.md") $out -Force
$docsDest = Join-Path $out "docs"
if (Test-Path $docsDest) { Remove-Item $docsDest -Recurse -Force }
Copy-Item (Join-Path $src "docs") $docsDest -Recurse

Write-Host ""
Write-Host "=== 全部编译完成！ ===" -ForegroundColor Green
Write-Host ""
Write-Host "  成品\GUI\CpuAffinityManager.Avalonia.exe   ← 推荐使用的图形界面"
Write-Host "  成品\GUI-WPF\CpuAffinityManager.App.exe    ← WPF 版图形界面"
Write-Host "  成品\MCP\CpuAffinityManager.Mcp.exe        ← AI(MCP) 命令行服务"
Write-Host ""
Write-Host "提示：使用 job-enforced / job-locked 锁定级别时，请右键『以管理员身份运行』。" -ForegroundColor Yellow
Start-Process explorer.exe $out
