# AI Agent Guide — CPU Affinity Manager

这份文档写给 AI Agent（Claude、Copilot 等）阅读。通过 MCP Server，你可以直接操作系统进程的 CPU 亲和性。

## 连接 MCP Server

在你的 MCP 客户端配置中添加：

```json
{
  "mcpServers": {
    "cpu-affinity": {
      "command": "CpuAffinityManager.Mcp.exe",
      "args": []
    }
  }
}
```

连接成功后，你会获得 **8 个工具**。

---

## 工具速查

### `get_topology` — 了解这台机器的 CPU 结构

```json
{}
```

返回示例：
```json
{
  "totalLogicalProcessors": 20,
  "pCoreCount": 8, "eCoreCount": 12,
  "smtEnabled": true,
  "socketCount": 1,
  "pCoreMask": "0x000FF",
  "eCoreMask": "0xFF000",
  "availableModes": ["all-cores","p-cores","e-cores",...]
}
```

**使用时机**：每次对话开始时先调用，了解当前机器有什么核心可用。

---

### `list_processes` — 查看正在运行的进程

```json
{
  "filter": "chrome*",    // 可选：通配符过滤进程名
  "top": 20               // 可选：最多返回数量，默认50
}
```

返回每个进程的 PID、名称、路径、当前亲和性掩码、优先级。

**使用时机**：用户说"看看什么在跑"、"找某个进程"。

---

### `get_rules` — 查看已配置的规则

```json
{}
```

返回所有规则的 ID、名称、匹配模式、亲和性模式、实施级别。

---

### `set_affinity` — 直接设置进程的 CPU 亲和性

这是最常用的工具。

```json
{
  "pid": 12345,
  "mode": "p-cores|first-half",
  "level": "hard-affinity",
  "socketIndex": null
}
```

#### mode 参数详解

**单模式**：
| 值 | 含义 |
|----|------|
| `all-cores` | 全部逻辑处理器 |
| `p-cores` | 仅大核/性能核 |
| `e-cores` | 仅小核/能效核 |
| `p-cores-smt` | 大核全部线程（含超线程） |
| `p-cores-no-smt` | 仅大核物理线程 |
| `first-half` | 逻辑编号前一半 |
| `second-half` | 逻辑编号后一半 |
| `custom` | 自定义掩码（配合 `customMask`） |

**组合/回退模式**（推荐）：

用 `|` 串联多个模式，从左到右依次尝试，使用第一个非零的结果：

| 写法 | 逻辑 |
|------|------|
| `p-cores\|first-half` | 有大核→用大核；没大核→用前一半核心 |
| `e-cores\|second-half` | 有小核→用小核；没小核→用后一半核心 |
| `p-cores\|e-cores\|all-cores` | 大核→小核→全核，三级回退 |
| `p-cores\|all-cores` | 有大核用大核，否则全核（最安全的可移植规则） |

**Socket 过滤**：

在模式后加 `@socketN` 限制到特定物理 CPU：

| 写法 | 含义 |
|------|------|
| `p-cores@socket0` | 第1个CPU的大核 |
| `all-cores@socket1` | 第2个CPU的全部核心 |
| `p-cores\|first-half@socket0` | 第1个CPU上：优先大核，否则前一半 |

#### level 参数

| 值 | 强制程度 | 进程能自己改吗 | 持久性 |
|----|---------|-------------|-------|
| `soft-cpu-sets` | 建议 | ✅ 能 | 进程存活期间 |
| `hard-affinity` | 强制 | ✅ 能（但需要主动改） | 进程存活期间 |
| `job-enforced` | 内核级强制 | ❌ 不能 | **内核对象，工具退出后仍存在** |
| `job-locked` | 最强锁定 | ❌ 不能（子进程也无法脱离） | **内核对象持久** |

**level 选择指南**：
- 普通程序 → `hard-affinity`
- 会自己改亲和性的程序（CPU-Z 等） → `job-enforced` 或 `job-locked`
- 反作弊/被保护的进程 → `job-enforced`（用 SeDebugPrivilege 绕过 ACL）
- 后台任务不需要严格限制 → `soft-cpu-sets`

#### 实战示例

```
用户："把这个游戏绑到大核上"
→ 先 get_topology 确认有大核（pCoreCount > 0）
→ 用 set_affinity {pid: 12345, mode: "p-cores", level: "job-enforced"}
→ 这一步需要管理员权限，内核对象持久生效

用户："把数据库绑到大核，如果没大核就前一半"
→ set_affinity {pid: 6789, mode: "p-cores|first-half", level: "hard-affinity"}

用户："阻止 CPU-Z 乱改亲和性"
→ set_affinity {pid: 4321, mode: "all-cores", level: "job-locked"}
→ CPU-Z 再也改不了自己的亲和性

用户："双路服务器，数据库只跑在第1个CPU的大核上"
→ get_topology 看 socketCount
→ set_affinity {pid: 9999, mode: "p-cores@socket0", level: "hard-affinity"}
```

---

### `apply_rule` — 按规则ID应用

```json
{
  "ruleId": "rule-001",
  "pid": 12345
}
```

前提是先通过 `get_rules` 知道有哪些规则。

---

### `scan_and_enforce` — 批量扫描匹配

```json
{}
```

扫描所有运行中的进程，把每条规则匹配到的进程都应用。返回影响了多少进程。

---

### `add_rule` — 添加规则

```json
{
  "name": "Chrome 用大核",
  "processPattern": "chrome.exe",
  "pathPattern": null,
  "mode": "p-cores|first-half",
  "level": "hard-affinity",
  "socketIndex": null,
  "lockBreakaway": false
}
```

字段说明：
- `name` — 规则名称（给人看）
- `processPattern` — 进程名通配符，支持 `*`, `?`, `|`, `[0-9]`
- `pathPattern` — 可选，路径通配符，`**` 匹配任意子目录
- `mode` — 同 `set_affinity` 的 mode
- `level` — 同 `set_affinity` 的 level
- `socketIndex` — 可选，多路CPU时指定 socket
- `lockBreakaway` — 是否阻止子进程脱离 Job Object

---

### `remove_rule` — 删除规则

```json
{
  "ruleId": "rule-003"
}
```

---

## 完整工作流

```
用户："帮我把这个新装的游戏优化一下"

Step 1: get_topology
  → 了解CPU：8P + 12E，有大小核

Step 2: list_processes {filter: "game*"}
  → 找到游戏进程 PID=5678

Step 3: set_affinity {pid: 5678, mode: "p-cores", level: "job-enforced"}
  → 游戏被锁定在大核上

Step 4: add_rule {
    name: "游戏绑定大核",
    processPattern: "game*.exe",
    pathPattern: "D:\\Games\\**",
    mode: "p-cores|first-half",
    level: "job-enforced"
}
  → 下次启动自动生效（配合 WMI 监控）
```

## 通配符语法

在 `processPattern` 和 `pathPattern` 中可用：

| 语法 | 匹配 | 示例 |
|------|------|------|
| `*` | 任意字符（不含 `\`） | `game*.exe` → `game2024.exe` |
| `**` | 跨目录任意字符 | `D:\Games\**` → 递归所有子目录 |
| `?` | 单个字符 | `app?.exe` → `app1.exe` |
| `\|` | OR | `a.exe\|b.exe` |
| `[...]` | 字符范围 | `app[0-9].exe` |

## 注意事项

1. **需要管理员权限** — `job-enforced` 和 `job-locked` 级别需要 `SeDebugPrivilege`
2. **先从简单开始** — 不确定用什么 level 时，先用 `hard-affinity`，不够再升级
3. **组合模式是可移植的** — 用 `p-cores|first-half` 写的规则，在 Intel 大小核和 AMD 全大核机器上都能正常工作
4. **规则顺序重要** — 第一条匹配的规则生效，后面的不再检查。把更具体的规则放在前面
5. **Job Object 是持久的** — 工具退出后限制仍在。要撤销只能重启目标进程
