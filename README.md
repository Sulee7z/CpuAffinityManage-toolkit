# CPU Affinity Manager

Windows CPU 亲和性管理工具。用于控制指定进程运行的 CPU 核心。

## 主要功能

* **核心绑定**：支持绑定进程到指定 CPU 核心。
* **防篡改**：通过内核 Job Object 机制限制进程自行修改亲和性。
* **规则自动匹配**：支持通配符与路径匹配，新进程启动时自动应用。
* **组合回退**：支持链式回退语法（如 `p-cores|first-half`），自动适配不同架构的 CPU。
* **AI 与 API 支持**：内置 MCP Server 与 HTTP API 接口，支持通过外部程序或内置助手生成与管理规则。

## 实施级别说明

| 级别 (`level`) | 实现机制 | 防篡改 |
| :--- | :--- | :---: |
| `soft-cpu-sets` | CPU Sets API | ❌ |
| `hard-affinity` | NtSetInformationProcess | ❌ |
| `job-enforced` | Job Object 限制 | ✅ |
| `job-locked` | Job Object 锁定 | ✅ |

## 核心模式 (`mode`)

* `p-cores` / `e-cores`：仅大核 / 仅小核
* `first-half` / `second-half`：逻辑编号前一半 / 后一半
* `all-cores`：全部核心
* `p-cores|first-half`：优先大核，无大核时回退至前一半核心

## 规则配置示例

```json
{
  "version": 2,
  "rules": [
    {
      "id": "rule-001",
      "name": "游戏绑定大核",
      "enabled": true,
      "match": {
        "process": "*.exe",
        "path": "**\\Games\\**",
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
