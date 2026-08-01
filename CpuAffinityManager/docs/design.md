# CPU Affinity Manager — C#/.NET 架构设计文档

## Context

开发类似 Process Lasso 的 CPU 亲和性管理工具，核心能力：
1. 能设置被保护进程（反作弊/ACL 剥离权限）的亲和性
2. 能阻止目标进程自行修改亲和性（防 CPUZ 类工具自改）
3. 利用 Windows 内核对象（Job Object）持久化，工具退出后限制仍存在
4. 规则系统支持通配符，WMI 实时监听新进程自动应用

## 技术选型

| 项 | 选择 | 理由 |
|----|------|------|
| 语言 | C# (.NET 10+) | P/Invoke 可调用所有 ntdll 函数，NativeAOT 支持 |
| GUI 框架 | WPF + Avalonia | WPF 原生 Windows 体验；Avalonia 跨平台 + NativeAOT 发布 |
| 进程监控 | WMI `ManagementEventWatcher` | 监听 `Win32_ProcessStartTrace`，工具运行时实时匹配 |
| AI 接口 | MCP Server (stdio) | Claude 等 AI Agent 可直接管理 CPU 亲和性 |
| 配置存储 | JSON (`System.Text.Json`) | 规则可导入导出，社区可共享 |
| 底层调用 | P/Invoke ntdll.dll + kernel32.dll | 绕过 Win32 API 限制 |

## 整体架构

```
┌─────────────────────────────────────────────────────────┐
│                    UI Layer                              │
│                                                          │
│  ┌──────────────────────┐  ┌──────────────────────────┐ │
│  │   WPF (App)           │  │   Avalonia (Avalonia)     │ │
│  │   MainWindow          │  │   MainWindow              │ │
│  │   ProcessListView     │  │   DashboardView           │ │
│  │   RuleEditorWindow    │  │   ProcessListView         │ │
│  │   AffinityMaskEditor  │  │   RuleListView            │ │
│  │   CoreTypeLegend      │  │   SettingsView            │ │
│  │   Code-behind MVVM    │  │   CommunityToolkit.Mvvm   │ │
│  └──────────┬───────────┘  └─────────────┬────────────┘ │
│             │                             │               │
│  ┌──────────┴─────────────────────────────┴──────────┐  │
│  │              MCP Server (AI Interface)              │  │
│  │  McpServer : get_topology / list_processes /       │  │
│  │  set_affinity / apply_rule / scan_and_enforce ...  │  │
│  └──────────────────────┬────────────────────────────┘  │
└─────────────────────────┼───────────────────────────────┘
                          │
┌─────────────────────────┴───────────────────────────────┐
│              Core Engine (.NET DLL)                      │
│                                                          │
│  IRuleEngine         IEnforcementService                 │
│  ICpuTopologyService IProcessMonitor                     │
│                                                          │
│  ┌─────────────┐ ┌────────────┐ ┌──────────────────┐   │
│  │ RuleEngine  │ │Enforcement │ │ CpuTopology       │   │
│  │ - Wildcard  │ │ - JobObj   │ │ - P/E/SMT/CCD   │   │
│  │ - Priority  │ │ - Affinity │ │ - Mask Build     │   │
│  │ - Fallback  │ │ - Relax    │ │ - Socket Filter  │   │
│  └─────────────┘ └────────────┘ └──────────────────┘   │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │ AffinityEnforcementWatchdog (250ms re-apply loop) │   │
│  └──────────────────────────────────────────────────┘   │
└──────────────────────┬───────────────────────────────────┘
                       │
┌──────────────────────┴───────────────────────────────────┐
│            Native Interop Layer                           │
│  NtdllImports.cs   Kernel32Imports.cs                     │
│  NativeStructs.cs  TokenPrivileges.cs                     │
│                                                           │
│  P/Invoke: ntdll!NtSetInformationProcess                  │
│            ntdll!NtQuerySystemInformation                  │
│            ntdll!NtOpenProcess                             │
│            kernel32!CreateJobObject                        │
│            kernel32!SetInformationJobObject                │
│            kernel32!AssignProcessToJobObject               │
└──────────────────────────────────────────────────────────┘
```

## Rule System Design

### 规则文件格式 (JSON)

```json
{
  "version": 2,
  "rules": [
    {
      "id": "rule-001",
      "name": "D盘游戏绑定大核",
      "enabled": true,
      "match": {
        "process": "*.exe",
        "path": "D:\\Games\\**",
        "exclude": ["*launcher*.exe", "*crash*.exe"]
      },
      "action": {
        "type": "cpu-affinity",
        "mode": "p-cores",
        "level": "job-enforced",
        "cpuPriority": "high"
      }
    },
    {
      "id": "rule-002",
      "name": "后台更新程序用小核",
      "enabled": true,
      "match": {
        "process": "update*.exe|setup*.exe|*indexer*.exe",
        "path": "**"
      },
      "action": {
        "type": "cpu-affinity",
        "mode": "e-cores",
        "level": "soft-cpu-sets"
      }
    },
    {
      "id": "rule-003",
      "name": "CPU-Z 反篡改",
      "enabled": true,
      "match": {
        "process": "cpuz*.exe|cpu-z*.exe"
      },
      "action": {
        "type": "cpu-affinity",
        "mode": "all-cores",
        "level": "job-enforced",
        "lock": true
      }
    },
    {
      "id": "rule-004",
      "name": "系统服务前一半核心",
      "enabled": true,
      "match": {
        "process": "svchost.exe|lsass.exe",
        "path": "C:\\Windows\\**"
      },
      "action": {
        "type": "cpu-affinity",
        "mode": "custom",
        "customMask": "0xFF",
        "level": "hard-affinity"
      }
    }
  ],
  "settings": {
    "enableWmiMonitor": true,
    "confirmBeforeApply": false,
    "minimizeToTray": true
  }
}
```

### 通配符语法

| 语法 | 匹配 | 示例 |
|------|------|------|
| `*` | 任意字符（不含 `\`） | `game*.exe` → `game2024.exe` |
| `?` | 单个字符 | `app?.exe` → `app1.exe` |
| `**` | 任意路径段（含多级目录） | `D:\Games\**` → 递归匹配所有子目录 |
| `\|` | OR 分隔 | `a.exe\|b.exe` → 匹配任一 |
| `[...]` | 字符范围 | `app[0-9].exe` |

### 匹配逻辑（first-match wins）

```
对每个进程 (pid, processName, fullPath)：
  foreach rule in rules (按数组顺序):
    if !rule.enabled → continue
    if !WildcardMatch(processName, rule.match.process) → continue
    if rule.match.path 且 !WildcardMatch(fullPath, rule.match.path) → continue
    if rule.match.exclude 任一匹配 → continue
    → 命中！执行 rule.action，停止匹配
```

### 预定义 Affinity Mode

| Mode | 说明 | 实现 |
|------|------|------|
| `all-cores` | 全部核心 | `~(0UL)` |
| `p-cores` | 仅大核 | EfficiencyClass ≥ 2 的逻辑处理器 |
| `e-cores` | 仅小核 | EfficiencyClass == 1 的逻辑处理器 |
| `p-cores-smt` | 大核+超线程 | P-core 的全部 SMT 线程 |
| `p-cores-no-smt` | 大核物理核 | P-core 每核仅取 SMT0 |
| `first-half` | 前一半 | 逻辑编号 0 ~ N/2-1 |
| `second-half` | 后一半 | 逻辑编号 N/2 ~ N-1 |
| `custom` | 自定义 | `customMask` 直接指定 |

### Enforcement Level

| Level | 机制 | 持久性 | 防自改 | 优先级最高 |
|-------|------|--------|--------|-----------|
| `soft-cpu-sets` | CPU Sets | 进程存活 | ❌ | 低 |
| `hard-affinity` | NtSetInformationProcess(0x15) | 进程存活 | ❌ | 中 |
| `job-enforced` | JobObject + Affinity Limit | **内核对象** | ✅ | **最高** |
| `job-locked` | JobObject + 禁止 Breakaway | **内核对象** | ✅ | **最高** |

## 亲和性配置优先级原理

### 层级关系

Windows 内核中，多种 CPU 限制机制可以同时作用在同一个进程上。当它们冲突时，内核按以下优先级链裁决：

```
┌──────────────────────────────────────────────────────────┐
│  优先级 1（最高）: Job Object Affinity Limit              │
│  ─────────────────────────────────────────────────────  │
│  设置者:  任何有 SeDebugPrivilege 的进程                  │
│  作用域:  Job 内所有进程 + 子进程（继承）                  │
│  持久性:  Job 是内核对象，创建者退出后仍存在               │
│  覆盖能力: 可覆盖进程自身设置、可拒绝进程修改请求            │
│  被覆盖:   不可被任何用户态 API 绕过                       │
│                                                          │
│  内核行为:                                                │
│  - 目标调用 SetProcessAffinityMask(非 Job 允许的核心)      │
│    → 内核返回 0xC0000719 (STATUS_PROCESS_IN_JOB)          │
│  - 目标调用 NtSetInformationProcess(0x15, mask)            │
│    → 内核校验 mask 必须是 Job 允许 mask 的子集，否则拒绝    │
│  - 子进程自动加入同一 Job，无法通过 Breakaway 脱离          │
└──────────────────────────────────────────────────────────┘
                          │ 覆盖
                          ▼
┌──────────────────────────────────────────────────────────┐
│  优先级 2: 进程亲和性掩码 (Process Affinity Mask)          │
│  ─────────────────────────────────────────────────────  │
│  设置者:  进程自身 或 有 PROCESS_SET_INFORMATION 权限者     │
│  作用域:  进程内所有线程                                   │
│  持久性:  进程存活期间                                     │
│  覆盖能力: 覆盖 CPU Sets（软性偏好）                        │
│  被覆盖:   被 Job Object Limit 覆盖                        │
│                                                          │
│  内核行为:                                                │
│  - NtSetInformationProcess(0x15, mask)                    │
│    → 内核更新 KPROCESS.AffinityMask                       │
│    → 调度器只把线程调度到 mask 指定的核心上                 │
│    → 如果线程有自己的 ThreadAffinity，取交集               │
└──────────────────────────────────────────────────────────┘
                          │ 覆盖
                          ▼
┌──────────────────────────────────────────────────────────┐
│  优先级 3: 线程亲和性掩码 (Thread Affinity Mask)            │
│  ─────────────────────────────────────────────────────  │
│  设置者:  线程自身 或 有 THREAD_SET_INFORMATION 权限者      │
│  作用域:  单个线程                                         │
│  持久性:  线程存活期间                                     │
│  覆盖能力: 进一步收窄进程亲和性（只能选择进程允许的核心）     │
│  被覆盖:   受限于进程亲和性掩码（取交集）                    │
│                                                          │
│  内核行为:                                                │
│  - 最终调度核心 = ProcessAffinity ∩ ThreadAffinity          │
│  - NtSetInformationThread(0x04, mask)                     │
│    → 内核校验 mask ⊆ ProcessAffinity，否则拒绝              │
│  - 线程继承进程亲和性作为默认值                             │
└──────────────────────────────────────────────────────────┘
                          │ 覆盖
                          ▼
┌──────────────────────────────────────────────────────────┐
│  优先级 4（最低）: CPU Sets（软性偏好）                     │
│  ─────────────────────────────────────────────────────  │
│  设置者:  进程自身 或 有权限的管理者                        │
│  作用域:  进程或线程                                       │
│  持久性:  进程存活期间                                     │
│  覆盖能力: 无 — 仅作为调度器的“建议”                       │
│  被覆盖:   被进程/线程亲和性掩码彻底覆盖                     │
│                                                          │
│  内核行为:                                                │
│  - SetProcessDefaultCpuSets(Sets)                        │
│    → 调度器优先考虑分配到 Sets 中的核心                     │
│    → 但如果 Set 中的核心全部繁忙，仍可调度到其他核心         │
│    → 这是"建议"而非"强制"                                  │
│  - 当进程亲和性掩码设置后，CPU Sets 实质上被忽略             │
└──────────────────────────────────────────────────────────┘
```

### 实际场景推演

#### 场景 A：Job Object 防 CPUZ 自改

```
初始状态:  无任何限制

步骤 1: 管理工具对 CPUZ 创建 Job Object
        SetInformationJobObject(JobCpuAffinityLimit, mask=0x00000FFF)  // P-cores
        AssignProcessToJobObject(hJob, cpuzProcess)
        
此时:     CPUZ 的调度范围被限制在 0xFFF 内
          KPROCESS.AffinityPadding? 不，Job 限制存储在 EJOB 结构中

步骤 2:  CPUZ 调用 SetProcessAffinityMask(0xFFFFF)  // 尝试绑定所有核心
          → 内核检查: 0xFFFFF ⊄ JobLimit(0x00000FFF)
          → 返回 0xC0000719 (STATUS_PROCESS_IN_JOB)
          → CPUZ 的修改失败！
          
结果:     CPUZ 被永久限制在 P-cores
          CPUZ 无法自行突破（没有用户态 API 可以绕过 Job Object 限制）
```

#### 场景 B：多层限制的叠加

```
初始状态:
  Job Limit:        0x00000FFF  (12 cores, P-cores only)
  Process Affinity: 0x000000FF  (8 cores, 进程自己设置)
  Thread Affinity:  0x0000003F  (6 cores, 线程设置)

最终调度范围:
  Job ∩ Process ∩ Thread = 0x00000FFF ∩ 0x000000FF ∩ 0x0000003F
                         = 0x0000003F  (6 cores)

每一层都进一步收窄了范围。
```

#### 场景 C：CPU Sets 被 Affinity 覆盖

```
初始状态:
  CPU Sets:     {0,1,2,3}  (偏好 P-cores 的前 4 个核心)
  
此时调度器行为:
  → 优先使用核心 0,1,2,3
  → 如果这 4 个核心全部满载，偶尔也会用到核心 4,5,6,7
  
设置 Affinity:
  NtSetInformationProcess(0x15, mask=0x0000000F)  (强制定在 0-3)
  
此时调度器行为:
  → CPU Sets 被忽略（affinity 已经限制到同样范围）
  → 如果 affinity 设置为 0x00000F00 (4-11)，CPU Sets 的偏好核心完全不可达，Sets 实际上无效
```

### 为什么要用 Job Object 而不是 Affinity

| 考量 | Process Affinity | Job Object |
|------|-----------------|------------|
| 设置权限 | 需要目标进程的 `PROCESS_SET_INFORMATION` | 需要 `SeDebugPrivilege`（与目标 ACL 无关） |
| 目标能否自己改 | ✅ 可以（调用 SetProcessAffinityMask） | ❌ 不行（内核拒绝） |
| 子进程 | 不影响 | 自动继承 |
| 持久性 | 进程退出则消失 | 内核对象，独立于创建者 |
| 能否突破 | 目标调用 ntdll 直调即可修改 | **无用户态 API 可绕过** |

### 反篡改的本质

很多人认为"提升 SeDebugPrivilege + 直接调用 ntdll"就可以对任何进程做任何事，但实际上：

- **Process Affinity 没有防篡改能力**：即使你用 ntdll 设置了目标的亲和性，目标自己随时可以再次调用 `NtSetInformationProcess(0x15)` 改回来
- **Job Object 有防篡改能力**：一旦进程被纳入设置了 CPU Affinity Limit 的 Job，目标调用 `NtSetInformationProcess` 时，内核会先检查 Job 限制再决定是否放行

这是为什么来掌芯说能"阻止 CPUZ 给自己设定亲和性"——唯一的解释就是使用了 Job Object。

### 策略选择指南

```
对普通进程:
  → hard-affinity (简单直接，进程一般不会自改)

对反作弊/被保护的进程:
  → job-enforced (用 SeDebugPrivilege + AssignProcessToJobObject 绕过 ACL)

对会自改亲和性的工具 (CPUZ, 游戏优化器等):
  → job-locked (只有 Job 能阻止自改)

对不需要严格绑定的后台任务:
  → soft-cpu-sets (节能，不强制)
```

## C# 核心实现

### 1. Native Interop (P/Invoke)

```csharp
// NativeStructs.cs
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_CPU_SET_INFORMATION
{
    public uint Size;
    public CPU_SET_INFORMATION_TYPE Type; // 0 = CpuSet
    public Union CpuSet;
    
    [StructLayout(LayoutKind.Explicit)]
    public struct Union
    {
        [FieldOffset(0)] public byte Id;
        [FieldOffset(0)] public ushort Core;
        [FieldOffset(0)] public uint LastLevelCache;
        // ... 更多字段
    }
}

public enum PROCESS_INFORMATION_CLASS : uint
{
    ProcessAffinityMask = 0x15,        // 未文档化
    ProcessDefaultCpuSets = 0x42,      // 未文档化
    ProcessPowerThrottling = 0x6D,
    ProcessEfficiencyMode = 0x6E
}

public enum SYSTEM_INFORMATION_CLASS : uint
{
    SystemBasicInformation = 0,
    SystemProcessorPerformanceInformation = 8,
    SystemCpuSetInformation = 0x49,    // 未文档化
}

// NtdllImports.cs
public static class NtdllImports
{
    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtSetInformationProcess(
        IntPtr ProcessHandle,
        PROCESS_INFORMATION_CLASS ProcessInformationClass,
        ref UIntPtr ProcessInformation,
        uint ProcessInformationLength);

    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtQuerySystemInformation(
        SYSTEM_INFORMATION_CLASS SystemInformationClass,
        IntPtr SystemInformation,
        uint SystemInformationLength,
        out uint ReturnLength);

    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtOpenProcess(
        out IntPtr ProcessHandle,
        uint DesiredAccess,
        ref OBJECT_ATTRIBUTES ObjectAttributes,
        ref CLIENT_ID ClientId);

    [DllImport("ntdll.dll", SetLastError = true)]
    public static extern int NtSetInformationThread(
        IntPtr ThreadHandle,
        uint ThreadInformationClass,
        ref UIntPtr ThreadInformation,
        uint ThreadInformationLength);
}

// Kernel32Imports.cs (Job Object 仍用 kernel32，因为 API 文档化且不会被 hook)
public static class Kernel32Imports
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
}
```

### 2. CPU Topology Service

```csharp
public interface ICpuTopologyService
{
    CpuTopology Detect();
    ulong BuildMask(CpuTopology topology, string mode, ulong? customMask = null);
}

public class CpuTopology
{
    public int TotalLogicalProcessors { get; init; }
    public int PcoreCount { get; init; }
    public int EcoreCount { get; init; }
    public bool SmtEnabled { get; init; }
    public ulong PcoreMask { get; init; }
    public ulong EcoreMask { get; init; }
    public ulong Smt0Mask { get; init; }   // 物理线程
    public ulong Smt1Mask { get; init; }   // 超线程
    public ulong Ccd0Mask { get; init; }   // AMD CCD0
    public ulong Ccd1Mask { get; init; }   // AMD CCD1
    public int SocketCount { get; init; }
    public List<ulong> SocketMasks { get; init; } = new();  // 多路服务器每路独立 mask

    public static readonly Dictionary<string, Func<CpuTopology, ulong>> MaskBuilders = new()
    {
        ["all-cores"]       = t => ~0UL,
        ["p-cores"]         = t => t.PcoreMask,
        ["e-cores"]         = t => t.EcoreMask,
        ["p-cores-smt"]     = t => t.PcoreMask,
        ["p-cores-no-smt"]  = t => t.PcoreMask & ~t.Smt1Mask,
        ["p-cores-first"]   = t => t.Smt0Mask & t.PcoreMask,
        ["first-half"]      = t => BuildHalfMask(t, firstHalf: true),
        ["second-half"]     = t => BuildHalfMask(t, firstHalf: false),
    };

    /// <summary>
    /// 组合回退模式：用 | 串联多个 mode，依次尝试，返回第一个非零 mask。
    /// 支持 socket 过滤：p-cores@socket0。
    /// 例：p-cores|first-half → 有大核用大核，没大核用前一半（通吃 Intel/AMD）
    /// </summary>
    public static ulong BuildMask(CpuTopology topology, string mode, ulong? customMask = null)
    {
        // 解析 @socketN 后缀
        // 处理 | 分隔的回退链
        // 每个子 mode 调用 BuildSingleMask，取第一个非零值
        // 通过 ApplySocketFilter 取 socket 交集
    }
}
```

### 3. Rule Engine

```csharp
public interface IRuleEngine
{
    RuleEntry? Match(string processName, string fullPath);
    void Load(string configPath);
    void Save(string configPath);
    void AddRule(RuleEntry rule);
    void RemoveRule(string ruleId);
    IReadOnlyList<RuleEntry> Rules { get; }
}

public class RuleEngine : IRuleEngine
{
    private List<RuleEntry> _rules = new();

    public RuleEntry? Match(string processName, string fullPath)
    {
        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;
            
            // process 字段必填，路径可选
            if (!Wildcard.Match(processName, rule.Match.Process, ignoreCase: true))
                continue;
                
            if (!string.IsNullOrEmpty(rule.Match.Path) &&
                !Wildcard.MatchPath(fullPath, rule.Match.Path, ignoreCase: true))
                continue;
                
            if (rule.Match.Exclude?.Any(ex =>
                Wildcard.Match(processName, ex, ignoreCase: true)) == true)
                continue;
                
            return rule;
        }
        return null;
    }
}
```

### 4. Wildcard Engine

```csharp
public static class Wildcard
{
    /// <summary>
    /// 匹配文件名（* 不跨路径分隔符）
    /// 支持: *, ?, | (OR), [chars], [range]
    /// </summary>
    public static bool Match(string input, string pattern, bool ignoreCase = true)
    {
        if (pattern.Contains('|'))
            return pattern.Split('|').Any(p => MatchSingle(input, p, ignoreCase));
        return MatchSingle(input, pattern, ignoreCase);
    }

    /// <summary>
    /// 匹配完整路径（** 跨目录）
    /// </summary>
    public static bool MatchPath(string path, string pattern, bool ignoreCase = true)
    {
        // 将 ** 转换为跨段匹配：
        // 1. 分割 pattern by **
        // 2. 每段顺序匹配 path 的对应部分
        var parts = pattern.Split(new[] { "**" }, StringSplitOptions.None);
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            // 将 pattern part 转为文件名通配符，在 path[pos..] 中匹配
            pos = MatchPathSegment(path, pos, parts[i], ignoreCase, 
                                   isLast: i == parts.Length - 1);
            if (pos < 0) return false;
        }
        return true;
    }

    // 内部实现：递归下降解析通配符 *
    private static bool MatchSingle(string input, string pattern, bool ignoreCase, 
                                     int si = 0, int pi = 0) { /* ... */ }
    private static int MatchPathSegment(string path, int start, string pattern, 
                                         bool ignoreCase, bool isLast) { /* ... */ }
}
```

### 5. Enforcement Service

```csharp
public interface IEnforcementService
{
    bool Apply(int pid, RuleEntry rule, CpuTopology topology);
    bool Relax(int pid, CpuTopology topology);       // 恢复全部核心
    bool ApplyJobEnforced(int pid, ulong mask);
    int ScanAndEnforce();                            // 返回受影响进程数
}

public class EnforcementService : IEnforcementService
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly Dictionary<int, IntPtr> _pidToJob = new();

    public bool Apply(int pid, RuleEntry rule, CpuTopology topology)
    {
        var mask = CpuTopology.MaskBuilders.TryGetValue(rule.Action.Mode, out var builder)
            ? builder(topology)
            : rule.Action.CustomMask ?? 0;

        if (mask == 0) return false;

        return rule.Action.Level switch
        {
            "soft-cpu-sets"    => ApplyCpuSets(pid, mask),
            "hard-affinity"    => ApplyHardAffinity(pid, mask),
            "job-enforced"     => ApplyJobEnforced(pid, mask),
            "job-locked"       => ApplyJobLocked(pid, mask),
            _ => false
        };
    }

    private bool ApplyJobEnforced(int pid, ulong mask)
    {
        // 1. 提升 SeDebugPrivilege
        TokenPrivileges.Enable(Privilege.SeDebug);
        
        // 2. 通过 NtOpenProcess 打开（绕过 kernel32 SA 检查）
        var hProcess = NativeProcess.Open(pid, 
            PROCESS_ACCESS.PROCESS_SET_INFORMATION | PROCESS_ACCESS.PROCESS_QUERY_INFORMATION);
        if (hProcess == IntPtr.Zero) return false;
        
        // 3. 创建或复用 Job Object
        var jobName = $"LzxCpuAffinity_Job_{pid}";
        var hJob = Kernel32Imports.CreateJobObject(IntPtr.Zero, jobName);
        if (hJob == IntPtr.Zero) return false;
        
        // 4. 设置 Job CPU Affinity Limit
        var limit = new JOBOBJECT_CPU_AFFINITY_LIMIT_INFORMATION
        {
            AffinityMask = mask,
            // EnableAffinity = 1 means enforce
        };
        using var pinned = new PinnedObject(limit);
        Kernel32Imports.SetInformationJobObject(hJob,
            JOBOBJECTINFOCLASS.JobObjectCpuAffinityLimitInformation,
            pinned.Ptr, (uint)Marshal.SizeOf<JOBOBJECT_CPU_AFFINITY_LIMIT_INFORMATION>());
        
        // 5. 将目标进程纳入 Job
        Kernel32Imports.AssignProcessToJobObject(hJob, hProcess);
        
        // 效果：目标进程调用 SetProcessAffinityMask 将被内核拒绝
        _pidToJob[pid] = hJob;
        return true;
    }
}
```

### 6. WMI 进程监控

```csharp
public class ProcessMonitor : IDisposable
{
    private ManagementEventWatcher? _watcher;

    public void Start(Action<ProcessStartEvent> onProcessStarted)
    {
        var query = new WqlEventQuery(
            "SELECT * FROM Win32_ProcessStartTrace");
        
        _watcher = new ManagementEventWatcher(query);
        _watcher.EventArrived += (sender, args) =>
        {
            var e = args.NewEvent;
            var pid = Convert.ToInt32(e.Properties["ProcessID"].Value);
            var name = e.Properties["ProcessName"].Value?.ToString() ?? "";
            
            // 延迟等待进程完全初始化
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(200); // 等待进程初始化
                onProcessStarted(new ProcessStartEvent(pid, name));
            });
        };
        _watcher.Start();
    }

    public void Stop() => _watcher?.Stop();
    public void Dispose() => _watcher?.Dispose();
}

public record ProcessStartEvent(int Pid, string ProcessName);
```

## C# 项目结构

```
CpuAffinityManager/
├── CpuAffinityManager.slnx
├── config/default-rules.json
├── src/
│   ├── CpuAffinityManager.Core/           # .NET Class Library
│   │   ├── CpuAffinityManager.Core.csproj
│   │   ├── Native/
│   │   │   ├── NtdllImports.cs
│   │   │   ├── Kernel32Imports.cs
│   │   │   ├── NativeStructs.cs
│   │   │   └── TokenPrivileges.cs
│   │   ├── Engine/
│   │   │   ├── IRuleEngine.cs
│   │   │   ├── RuleEngine.cs
│   │   │   ├── RuleEntry.cs
│   │   │   ├── RuleConfig.cs
│   │   │   ├── RuleConfigPath.cs
│   │   │   └── Wildcard.cs
│   │   ├── Cpu/
│   │   │   ├── ICpuTopologyService.cs
│   │   │   ├── CpuTopologyService.cs
│   │   │   └── CpuTopology.cs
│   │   ├── Enforcement/
│   │   │   ├── IEnforcementService.cs
│   │   │   ├── EnforcementService.cs
│   │   │   ├── JobObjectManager.cs
│   │   │   └── AffinityEnforcementWatchdog.cs
│   │   ├── Monitoring/
│   │   │   ├── IProcessMonitor.cs
│   │   │   ├── WmiProcessMonitor.cs
│   │   │   └── ProcessSearch.cs
│   │   └── LogConfig.cs
│   │
│   ├── CpuAffinityManager.App/           # WPF Application
│   │   ├── CpuAffinityManager.App.csproj
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── ProcessListViewModel.cs
│   │   │   ├── RuleEditorViewModel.cs
│   │   │   └── CpuTopologyViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.xaml / .cs
│   │   │   └── RuleEditorWindow.xaml / .cs
│   │   ├── Controls/
│   │   │   ├── AffinityMaskEditor.xaml / .cs
│   │   │   └── CoreTypeLegend.xaml / .cs
│   │   ├── Converters/
│   │   │   ├── MaskToBitArrayConverter.cs
│   │   │   └── RuleLevelToColorConverter.cs
│   │   └── Themes/
│   │       └── ModernTheme.xaml
│   │
│   ├── CpuAffinityManager.Avalonia/      # Avalonia UI Application (NativeAOT)
│   │   ├── CpuAffinityManager.Avalonia.csproj
│   │   ├── App.axaml / App.axaml.cs
│   │   ├── Program.cs
│   │   ├── ViewLocator.cs
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── DashboardViewModel.cs
│   │   │   ├── ProcessListViewModel.cs
│   │   │   ├── RuleListViewModel.cs
│   │   │   ├── SettingsViewModel.cs
│   │   │   └── ViewModelBase.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml / .cs
│   │   │   ├── DashboardView.axaml / .cs
│   │   │   ├── ProcessListView.axaml / .cs
│   │   │   ├── RuleListView.axaml / .cs
│   │   │   ├── RuleEditorWindow.axaml / .cs
│   │   │   └── SettingsView.axaml / .cs
│   │   └── Assets/
│   │       ├── Colors.axaml
│   │       └── app.ico
│   │
│   └── CpuAffinityManager.Mcp/           # MCP Server (AI Agent 接口)
│       ├── CpuAffinityManager.Mcp.csproj
│       ├── Program.cs
│       └── McpServer.cs
│
├── tests/
│   └── CpuAffinityManager.Tests/
│       ├── WildcardTests.cs
│       ├── RuleEngineTests.cs
│       ├── RuleConfigPathTests.cs
│       ├── CpuTopologyTests.cs
│       ├── ProcessSearchTests.cs
│       └── McpIntegrationTests.cs
│
└── docs/
    ├── design.md
    └── ai-guide.md
```

## 关键流程

### 规则开关 (Toggle ON/OFF)
```
用户切换规则启用/禁用
  → RuleItem.OnEnabledChanged → RuleListViewModel.HandleRuleToggled
    → 更新 RuleEntry.Enabled → SaveRules()
    → ApplyRuleToggleToRunningProcesses(rule, enabled):
      ON:  扫描所有进程 → 匹配的进程应用亲和性
      OFF: 扫描所有进程 → 有替换规则则应用替换 → 无则 Relax（恢复全部核心）
```

### 手动应用流程
```
用户选中进程 → 右键菜单 → 选择操作
  → EnforcementService.Apply(pid, rule, topology)
    → 根据 enforcement level:
        soft:      NtSetInformationProcess(0x42) CPU Sets
        hard:      NtSetInformationProcess(0x15) Affinity Mask
        job:       CreateJobObject → SetInformationJobObject → AssignProcessToJobObject
```

### WMI 自动匹配流程
```
WMI ProcessStartTrace 事件触发
  → ProcessMonitor 回调
    → ThreadPool 延迟 200ms
      → 获取进程完整路径 (NtQueryInformationProcess + QueryFullProcessImageName)
      → RuleEngine.Match(processName, fullPath)
        → 命中规则
          → EnforcementService.Apply(pid, rule, topology)
            → 记录到日志
```

### Job Object 反篡改流程
```
1. CreateJobObject(name: "LzxCpuAffinity_Job_{PID}")
2. SetInformationJobObject(JobObjectCpuAffinityLimitInformation, mask=P-core mask)
3. SetInformationJobObject(JobObjectBasicLimitInformation, 
      LimitFlags=JOB_OBJECT_LIMIT_BREAKAWAY_OK=0)  // 禁止子进程脱离
4. AssignProcessToJobObject(hJob, hTarget)
5. [管理工具可以退出了]
   
结果：
  - 目标进程调用 SetProcessAffinityMask(E-core) → 内核拒绝 (STATUS_PROCESS_IN_JOB)
  - 目标进程的子进程自动继承 Job 限制
  - 管理工具退出后 Job 对象仍在内核中存在
```

### Watchdog 保活流程
```
AffinityEnforcementWatchdog（250ms 循环）
  → 遍历所有受管进程
    → 检测进程是否仍存活（否则清理 Job 句柄）
    → 检测进程亲和性是否被篡改
    → 如被篡改 → 重新 Apply 规则
```

### 突破受保护进程
```csharp
// 标准 kernel32 OpenProcess 会被 ACL 拒绝
// 改用 ntdll 直接调用
bool OpenProtectedProcess(int pid, out IntPtr hProcess)
{
    TokenPrivileges.Enable(Privilege.SeDebug); // 获取调试权限
    
    var oa = new OBJECT_ATTRIBUTES();
    var cid = new CLIENT_ID { UniqueProcess = (IntPtr)pid, UniqueThread = IntPtr.Zero };
    
    int status = NtdllImports.NtOpenProcess(
        out hProcess,
        PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
        ref oa, ref cid);
    
    return status == 0; // STATUS_SUCCESS
}
```

## 验证方案

1. **通配符匹配**：单元测试 `*`, `**`, `?`, `|`, `[...]` 及 exclude 逻辑
2. **CPU 拓扑**：Intel 12-14 代 + AMD 双 CCD 机器验证 P/E/SMT/CCD 分类正确性
3. **反篡改测试**：
   - 对 CPUZ 应用 `job-enforced` 规则
   - CPUZ 尝试改自身亲和性 → 验证被内核拒绝
4. **受保护进程测试**：
   - 对反作弊游戏应用亲和性规则
   - 验证 NtOpenProcess 成功而 kernel32 OpenProcess 失败
5. **持久化测试**：
   - 管理工具退出
   - 验证 Job Object 仍存在（`tasklist /m /fi "imagename eq target.exe"`）
6. **WMI 监控测试**：
   - 启动新进程 → 验证规则自动应用
