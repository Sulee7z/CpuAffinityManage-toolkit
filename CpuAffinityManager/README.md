# CPU Affinity Manager

Windows CPU 亲和性管理工具 — 自由控制哪个进程跑在哪些核心上。

## 它能做什么

- **绑定进程到指定核心** — 游戏绑大核，后台绑小核，数据库绑前一半
- **阻止程序自己改亲和性** — 内核级 Job Object 锁定，CPU-Z 类工具改不了
- **规则自动匹配** — 通配符匹配 + WMI 实时监控，新进程启动自动应用
- **一次写好到处跑** — 组合回退模式，同一规则在 Intel 大小核和 AMD 全大核机器上都能正确工作
- **AI 可操控** — 内置 MCP Server，Claude 等 AI 可以直接管理 CPU 亲和性

## 核心原理

### 优先级链

Windows 内核中同时存在多种 CPU 限制机制，冲突时按优先级裁决：

```
优先级 1（最高）: Job Object Affinity Limit
  ├─ 内核对象，创建者退出后仍存在
  ├─ 可覆盖进程自身设置
  └─ 目标进程无法用任何用户态 API 绕过

优先级 2: Process Affinity Mask
  └─ NtSetInformationProcess(0x15) 设置，进程存活期间有效

优先级 3: Thread Affinity Mask
  └─ 进一步收窄进程亲和性，最终调度核心 = Process ∩ Thread

优先级 4（最低）: CPU Sets（软性偏好）
  └─ 仅作为调度器的"建议"，不是强制
```

**关键结论**：只有 Job Object 能阻止目标进程自己改亲和性。

### 为什么 Process Affinity 不够

即使你用 `SeDebugPrivilege` + ntdll 设置了目标的亲和性，目标进程随时可以自己再改回来。Job Object 在内核层面拦截：目标调用 `NtSetInformationProcess` → 内核先检查 Job 限制 → 不在允许范围内则拒绝。

## 功能

### 亲和性模式

| 模式 | 说明 |
|------|------|
| `p-cores` | 仅大核 / 性能核 |
| `e-cores` | 仅小核 / 能效核 |
| `all-cores` | 全部逻辑处理器 |
| `p-cores-smt` | 大核全部线程（含超线程） |
| `p-cores-no-smt` | 仅大核物理线程 |
| `first-half` | 逻辑编号前一半 |
| `second-half` | 逻辑编号后一半 |
| `custom` | 自定义十六进制掩码 |

### 组合回退模式（自动适配不同机器）

用 `|` 串联，依次尝试，返回第一个非零结果：

```
p-cores|first-half     → 有大核用大核，没大核用前一半（写一次，通吃 Intel/AMD）
e-cores|second-half    → 有小核用小核，没小核用后一半
p-cores|e-cores|all-cores  → 三级回退
```

### Socket 过滤（多路服务器）

```
p-cores@socket0        → 第1个物理CPU的大核
all-cores@socket1      → 第2个物理CPU的全部核心
```

### 实施级别

| 级别 | 机制 | 持久性 | 防自改 | 适用场景 |
|------|------|--------|--------|---------|
| `soft-cpu-sets` | CPU Sets | 进程存活 | ❌ | 后台任务，不需要严格绑核 |
| `hard-affinity` | NtSetInformationProcess(0x15) | 进程存活 | ❌ | 普通程序 |
| `job-enforced` | Job Object | **内核对象持久** | ✅ | 会自己改亲和性的程序 |
| `job-locked` | Job Object + 禁止脱离 | **内核对象持久** | ✅ | CPU-Z 等反篡改场景 |

### 通配符规则

| 语法 | 匹配 | 示例 |
|------|------|------|
| `*` | 任意字符（不含 `\`） | `game*.exe` → `game2024.exe` |
| `**` | 跨目录任意字符 | `D:\Games\**` → 递归所有子目录 |
| `?` | 单个字符 | `app?.exe` → `app1.exe` |
| `\|` | OR 分隔 | `a.exe\|b.exe` |
| `[...]` | 字符范围 | `app[0-9].exe` |

### 规则匹配逻辑

```
对于每个进程(进程名, 完整路径):
  按顺序遍历每条规则:
     规则未启用 → 跳过
     进程名不匹配通配符 → 跳过
     指定了路径但路径不匹配 → 跳过
     匹配到排除列表 → 跳过
     → 命中！执行规则，停止匹配
```

## 快速开始

### 编译

```bash
dotnet build -c Release
```

输出在 `src/CpuAffinityManager.App/bin/Release/net10.0-windows/`。

### GUI

运行 `CpuAffinityManager.App.exe`。左侧导航切换 Dashboard / Processes / Rules / Settings。

### 命令行 / AI 操控

```bash
# 启动 MCP Server（stdio 模式，供 AI Agent 使用）
CpuAffinityManager.Mcp.exe
```

MCP 工具列表：

| 工具 | 功能 |
|------|------|
| `get_topology` | 获取 CPU 拓扑（P/E核数、Socket数） |
| `list_processes` | 列出运行进程及当前亲和性 |
| `get_rules` | 查看已配置的规则 |
| `set_affinity` | 直接设置进程 CPU 亲和性 |
| `apply_rule` | 按规则 ID 应用到进程 |
| `scan_and_enforce` | 批量扫描全部进程并应用规则 |
| `add_rule` | 添加规则 |
| `remove_rule` | 删除规则 |

AI 使用指南见 [docs/ai-guide.md](docs/ai-guide.md)。

### 规则文件

规则存储在 JSON 文件中（默认 `config/default-rules.json`）：

```json
{
  "version": 2,
  "rules": [
    {
      "id": "rule-001",
      "name": "游戏绑定大核（无大核则前一半）",
      "enabled": true,
      "match": {
        "process": "*.exe",
        "path": "D:\\Games\\**",
        "exclude": ["*launcher*.exe"]
      },
      "action": {
        "type": "cpu-affinity",
        "mode": "p-cores|first-half",
        "level": "job-enforced"
      }
    }
  ]
}
```

## 场景速查

| 需求 | mode | level |
|------|------|-------|
| 游戏用大核 | `p-cores` | `job-enforced` |
| 后台更新用小核 | `e-cores\|second-half` | `soft-cpu-sets` |
| 数据库用大核（兼容AMD） | `p-cores\|first-half` | `hard-affinity` |
| 阻止CPUZ改亲和性 | `all-cores` | `job-locked` |
| 系统服务前一半核心 | `first-half` | `hard-affinity` |
| 双路服务器第1路大核 | `p-cores@socket0` | `hard-affinity` |
| 可移植规则（通吃所有机器） | `p-cores\|first-half` | `job-enforced` |

## 更新记录（v2.5 新增）

- **线程级亲和性管理**：新增 `list_threads`（MCP）与 `GET /api/processes/{pid}/threads`（HTTP）查看进程每个线程的亲和掩码 / 理想核 / 占用；`set_thread_affinity`（MCP）与 `POST /api/threads/affinity`（HTTP）可单独钉住某个线程（掩码 `0` = 还原为全核）。Avalonia 进程页右键新增「线程详情（亲和性/理想核）」。
- **规则导入导出（AI 通道）**：MCP 新增 `export_rules` / `import_rules`，HTTP 新增 `GET /api/rules/export` 与 `POST /api/rules/import`（`replace` 参数选择替换全部或合并；合并时相同 ID 覆盖）。界面上的导入/导出按钮保留。
- **仪表盘实时负载**：Avalonia 仪表盘新增 CPU 占用率与物理内存（已用/总量）实时卡片（2 秒采样，原生 API，无 WMI 开销）。
- **进程列表新增「过滤」下拉**：全部进程 / 已匹配规则 / Job 强制（此前下拉框已存在但不生效）。
- 版本统一为 v2.5.0（此前界面显示 v2.20.0、API 返回 2.4.0、MCP 返回 1.0.0，三处不一致）。

## 更新记录（v2.5 修复的 Bug）

- **修复：受保护进程命中不了纯进程名规则**（WMI 自动应用路径）：`QueryFullProcessImageName` 失败时改为按空路径继续匹配，与 `ScanAndEnforce` 行为一致（WPF / Avalonia / MCP 三处）。
- **修复：socket 过滤规则被看门狗每秒反复重打**：看门狗计算期望掩码时未附加 `@socketN` 后缀，与 `Apply()` 实际执行的掩码不一致，导致每 tick 误判并重打 + 刷日志。
- **修复：Job 对象句柄泄漏 + PID 复用误伤**：`JobObjectManager` 从不释放已退出进程的 Job 句柄（长会话句柄/内核对象无限增长）；现在看门狗每轮用存活 PID 清理，且 `GetOrCreateJob` 检测到 PID 被复用时会先释放旧 Job，避免新进程继承旧亲和性限制。`PersistentAffinityStore` 的硬锁标记同样随进程退出清理。
- **修复：Avalonia 跨线程更新 UI**：WMI 回调、`ScanAndApplyNow`、规则切换扫描在后台线程写 `StatusText` / 进程列表（`async void`），现在统一封送到 UI 线程。
- **修复：规则开关直接改写共享快照**：Avalonia 规则页与 WPF 规则勾选改为通过 `AddRule` 发布新副本（copy-on-write），不再原地修改看门狗正在读取的不可变快照；单条规则切换也不再触发重复的全量扫描。
- **修复：`first-half`/`second-half` 掩码在 1 核与 >64 核机器上错误**：单核机器 `first-half` 之前返回 0；奇数核心现在把多余核分给前一半；>64 逻辑核时保证不溢出、不越界。
- **修复：>64 核混合架构机器 P/E 核检测被整体丢弃**：`QueryCpuSetEfficiency` 拿到部分（group 0）数据后被 `Clear()` 清空，退化为"全是大核"；现在保留有效数据。
- **修复：EcoQoS 开关会覆盖用户设置的 CPU 优先级**：关闭效率模式时不再把「高/高于常规」优先级强行改回常规；仅当进程仍处于 Idle 时才还原为 Normal。
- **修复：`reg.exe`/`powercfg`/`netstat` 超时形同虚设**：同步 `ReadToEnd()` 在 `WaitForExit(超时)` 之前调用，子进程挂起会无限阻塞调用线程（可卡 UI）；改为异步读 + 超时杀进程。
- **修复：MCP `list_processes` 用慢且易抛异常的 `Process.MainModule`**：改用原生 `QueryFullProcessImageName`（含 512 字符深路径自动扩容）。
- **修复：AI 提示词缺大括号**：`BuildPrompt` 中 `if (games)` 之后的追加行无条件执行。
- **修复：HTTP API 异常时响应未关闭 / 并发无上限**：catch 分支保证关闭响应；请求处理加 32 路并发上限。
- **修复：WMI watcher 启动失败泄漏**：`watcher.Start()` 抛异常时释放已创建的 watcher；`Dispose` 后不再投递延迟回调。
- **修复：通配符路径匹配不支持 `|` 多路径**：`MatchPath` 现在支持 `D:\Games\**|E:\Games\**` 式 OR 替代（与进程名通配符一致）。
- **修复：过时测试**：`RuleEngineTests` 断言 rule-003 为 CPU-Z 规则（与现行默认规则不符）；`McpIntegrationTests` 未统计 `list_drives` 等新工具；`RuleConfigPathTests` 在 LocalAppData 已有副本时必然失败——均已修正。

## 更新记录（v2.4 新增·专业版功能第一批）

- **进程动作与优先级**(进程页右键菜单):挂起 / 恢复 / 结束进程、CPU 优先级(高/常规/低)、IO 优先级(低/常规)、内存优先级(低)、效率模式 EcoQoS(开/关)、动态优先级。
- **内存**:右键「释放物理内存(清空工作集)」;「系统工具」页「一键系统内存清理」(清空所有进程工作集)。
- **系统级调优**(新增「系统工具」页):计时器分辨率(0.5/1.0/15.6 ms)、电源模式一键切换(平衡/高性能/节能/卓越性能)、前台优先级分离(前台加速/均衡,改 Win32PrioritySeparation)。
- **进程信息与文件**:右键「定位文件」(资源管理器定位)、「查看句柄/线程/联网信息」(句柄数、线程数、内存、TCP/UDP 连接)。
- **规则可带专业动作**:规则的 action 新增 `ioPriority`、`memoryPriority`、`efficiencyMode` 三个可选字段,扫描/自动匹配/AI 生成时会一并应用(AI 助手已知晓这些字段)。
- 说明:GPU 优先级(Windows 无公开 API)、网络传出带宽限制(需 QoS/驱动)、ESET 本地查杀引擎(第三方商业引擎)本版不做;多数系统级操作需以管理员身份运行。

## 更新记录（v2.3 新增）

- **导入第三方 AI 的 API Key,让 AI 帮你写规则**:新增「AI 助手」页面。填入你自己的 API Key(兼容 OpenAI 接口:OpenAI / DeepSeek / Kimi(Moonshot) / 智谱 GLM / OpenRouter / 本地 Ollama,内置提供商预设一键填好 Base URL 与模型),用一句话描述需求(如「把所有盘上的游戏绑大核防篡改,后台更新和网盘放小核」),AI 会生成规则 JSON 并**直接导入**到规则列表。API Key 保存在本机 `%LOCALAPPDATA%\CpuAffinityManager\ai-config.json`。
  - 注意区分两个方向:v2.1 的 HTTP API 是「让第三方 AI 调用本软件」;本次是「本软件调用第三方 AI」——你要的导入 apikey 就是后者。两者都保留。

## 更新记录（v2.2 新增）

- **界面按 Windows 11 Fluent 风格重构（Avalonia 版）**：Mica 云母背景 + 扩展标题栏、NavigationView 侧边导航（选中项左侧强调条 + Segoe Fluent 图标）、8px 圆角卡片、系统强调色按钮。配色改为 Win11 调色板并**跟随系统浅色/深色自动切换**（用主题字典 + DynamicResource,系统切换主题时实时变色）。
- **第三方 AI API 直接内置到界面**:API 服务从 MCP 移到 Core,现在在「设置 → 第三方 AI 接口」里可直接**一键开关**,设置端口、是否允许远程,启动后显示访问地址和给 AI 的调用示例(可选中复制)。无需再单独跑命令行。MCP 的 `--http` 命令行方式仍保留。
- **修复:主题下拉框现已生效** —「设置 → 外观 → 主题」选择「浅色/深色/跟随系统」会立即切换整个界面(之前只存了值未应用)。
- **API 新增网页控制台** — 用浏览器打开 API 地址(如 `http://127.0.0.1:8088/`)会显示一个中文网页控制台(查看拓扑/进程、增删规则、一键扫描),跟随系统深浅色;而用程序/AI 调用(非浏览器)访问根路径仍返回 JSON 清单,JSON 清单也可从 `/api` 获取。
- 版本号更新为 v2.2。

## 更新记录（v2.1 新增）

- **界面全局中文汉化**：Avalonia 与 WPF 两套界面的所有可见文字、右键菜单、状态提示、规则编辑器均已中文化。
- **白底黑字**：主文字颜色改为近黑 `#111111`，白色背景下对比更清晰。
- **第三方 AI HTTP API**：MCP 程序新增 REST 接口，第三方 AI 可通过 HTTP 读取拓扑/进程并**自动写规则**。
  启动：`CpuAffinityManager.Mcp.exe --http`（默认 `127.0.0.1:8088`，可加端口号；`--allow-remote` 监听所有网卡）。
  主要端点：`GET /`(接口清单) `GET /api/topology` `GET /api/drives` `GET /api/processes` `GET /api/rules`
  `POST /api/rules`(增改规则) `DELETE /api/rules/{id}` `POST /api/affinity` `POST /api/scan`。
- **内置更多规则**：默认规则扩充到 16 条，覆盖 Steam/Epic/通用游戏目录、模拟器、浏览器、Adobe、渲染转码、编译开发、数据库、压缩、后台更新、云盘、即时通讯、杀毒等场景。
- **目录自动识别所有盘**：游戏等路径规则改用 `**\...` 全盘通配（如 `**\steamapps\common\**`、`**\Games\**`），无需再写死盘符；新增 `list_drives`（MCP）与 `/api/drives`（HTTP）枚举所有固定盘。
- **关闭最小化**：Avalonia 版新增系统托盘图标，关闭窗口时最小化到托盘（托盘菜单可「显示主窗口 / 退出」）；WPF 版关闭按钮改为最小化（Alt+F4 真正退出）。可在「设置」中开关。
- **修复关闭后 Windows 目录权限问题**：规则与日志改写到 `%LOCALAPPDATA%\CpuAffinityManager`，不再写入安装目录/Program Files（避免提权运行后 ACL 变更导致普通权限无法读写）；退出时自动释放所有 Job 对象亲和性限制，不再给系统进程残留内核级限制。

## 更新记录（v2.0 优化）

- **移除调试代码**：删除 Serilog 的 Debug 输出 sink 与 `Serilog.Sinks.Debug` 依赖、Avalonia DevTools（`Avalonia.Diagnostics`）、`LogToTrace`；日志最低级别升到 Information，只写文件。
- **性能优化**
  - 通配符匹配（每进程每规则的热路径）改为基于 `Span` 的零分配 `|` 分支匹配。
  - 规则引擎改为不可变快照 + 写时复制：匹配时无锁、不再每次拷贝规则列表。
  - 看门狗轮询由 250ms 放宽到 1000ms（后台扫描频率降为原来的 1/4）；取进程路径改用原生 `QueryFullProcessImageName`，替换缓慢且频繁抛异常的 `Process.MainModule`；仅当存在含路径条件的规则时才查询路径。
  - `ScanAndEnforce` 同样改用原生取路径，并修正原先"取不到路径就跳过"导致受保护进程无法命中纯进程名规则的问题。
  - 仪表盘画刷改为一次性创建并冻结（WPF `Freeze()` / Avalonia `ToImmutable()`）复用。
- **精简事件函数**：WPF 进程右键菜单的 7 个 `Ctx*_Click` 处理器合并为 1 个数据驱动的 `ProcessMenuItem_Click`，右键菜单只构建一次后复用。

## 系统要求

- Windows 10/11（需要 WMI 和 Job Object API）
- .NET 10 Runtime
- 管理员权限（`job-enforced` 和 `job-locked` 级别）

## 项目结构

```
CpuAffinityManager/
├── config/default-rules.json          # 默认规则
├── docs/
│   ├── design.md                      # 架构设计文档
│   └── ai-guide.md                    # AI Agent 使用指南
├── src/
│   ├── CpuAffinityManager.Core/       # 核心引擎 DLL
│   ├── CpuAffinityManager.App/        # WPF GUI
│   ├── CpuAffinityManager.Avalonia/   # Avalonia GUI（跨平台）
│   └── CpuAffinityManager.Mcp/        # MCP Server
└── tests/
    └── CpuAffinityManager.Tests/      # 单元测试
```
