CPU Affinity Manager — 一键编译包使用说明
=========================================

这个压缩包里是完整源代码 + 自动编译脚本。
只需两步，就能在你的 Windows 电脑上编译出可运行的软件。

【第 1 步】解压本压缩包到任意文件夹（路径最好不含特殊符号）

【第 2 步】双击运行「build.cmd」

脚本会自动完成以下事情：
  1. 检测电脑上是否有 .NET 10 SDK；
     没有的话会尝试用 winget 自动安装（需要联网）；
     如果自动安装失败，会打开官方下载页面，手动安装后再运行一次即可：
     https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0
     （下载「SDK x64 安装程序」）
  2. 自动编译出三个程序，放在「成品」文件夹：
     成品\GUI\CpuAffinityManager.Avalonia.exe   ← 图形界面（推荐）
     成品\GUI-WPF\CpuAffinityManager.App.exe    ← WPF 版图形界面
     成品\MCP\CpuAffinityManager.Mcp.exe        ← AI(MCP) 服务，供 Claude 等 AI 调用
  3. 编译完成后自动打开「成品」文件夹。

编译好的 exe 是自包含的，可以复制到其他 Windows 10/11 x64 电脑
直接运行，不需要再装 .NET。

【使用提示】
- 「job-enforced」和「job-locked」这两个锁定级别需要管理员权限：
  右键 exe →「以管理员身份运行」。
- 规则保存在程序目录的 config\default-rules.json，可以直接编辑。
- 软件功能与原理详见「CpuAffinityManager」文件夹里的 README.md 和 docs\ 目录。

【常见问题】
Q: 双击脚本一闪而过？
A: 右键「build.cmd」→ 以管理员身份运行；或先手动安装 .NET 10 SDK。

Q: 提示 PowerShell 禁止运行脚本？
A: 脚本已带 -ExecutionPolicy Bypass 参数，一般不会出现；
   如仍出现，请在 PowerShell 中手动执行：
   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

Q: 编译要多久？
A: 首次编译需要联网下载依赖包，大约 2~5 分钟；之后再编译会快很多。

Q: 解压后文件名乱码？
A: 本包所有文件夹/脚本均已改用英文名，不会再乱码。
   若你的旧压缩软件仍有问题，改用 Windows 自带解压或 7-Zip 即可。
