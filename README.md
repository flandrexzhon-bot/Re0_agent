<div align="center">

<img src="icon.png" alt="Re:Zero 命运全书" width="180" />

# Re:Zero 命运全书

**一款以《Re:从零开始的异世界生活》为舞台的单机 AI 跑团 RPG**

多智能体 LLM 驱动 · 死亡回归 · 骰子判定 · 存档 / 分支时间线

</div>

---

## 这是什么

「命运全书」是一个运行在 Windows 桌面上的单机文字冒险 / 跑团游戏。你扮演穿越到异世界的主角，由多个大语言模型（LLM）智能体分别扮演 **游戏主持人（GM）**、**世界中的各个 NPC 角色**，围绕你的抉择推进剧情。系统内建 Re:Zero 世界观设定书、角色属性、骰子判定与「死亡回归」机制，把原作的核心体验做成了可反复游玩的沙盒。

一句话：一本会自己写下去的、可以存档回溯、可以「死了重来」的异世界命运之书。

## 核心玩法

- **六段式大回合**：每个大回合按固定流程推进 —— GM 开场 → 等你落笔抉择 → 主角行动 → 调度 NPC → NPC 依序回应 → 填表结算存档。状态机持久化到数据库，关掉软件再打开能精确从断点继续。
- **重 roll / swipe（变体）**：对任何一段不满意都能重新生成，像 SillyTavern 那样左右滑动切换变体。开场、NPC 回复、填表记录的全部 roll 记录都会长期保留。
- **Fork / 分支时间线**：从任意历史回合分叉出平行存档，探索「如果当时那样选会怎样」。
- **死亡回归**：主角死亡后回到之前的存档点，保留元记忆，重走命运 —— 原作的核心设定。
- **骰子判定与战斗**：内建骰子表格（`骰子表格SQL_v4.1.json`）+ 角色属性系统，行动与战斗以掷骰结算。
- **世界观 RAG**：内置 4MB+ 的 Re:Zero 设定书（`re0从零开始的异世界生活.json`），按场景检索相关词条注入提示词，让 NPC 言行贴合原作。
- **章节切换**：随剧情推进在原作关键章节（1 / 7 / 18 / 53 / 82 等）间切换世界状态。

## 技术架构

```
Re0Agent.sln
├── src/Re0Agent.App    —— MAUI Blazor Hybrid（Windows 桌面壳 + Blazor WebView UI）
├── src/Re0Agent.Core   —— 领域核心：智能体、LLM 客户端、骰子、存档、RAG、EF Core 实体
└── src/Re0Agent.Tests  —— 单元测试
```

- **UI**：.NET 8 MAUI Blazor Hybrid（`net8.0-windows10.0.19041.0`，非打包 Win32 exe）。前端用 Blazor 组件写在 WebView 里。
- **持久化**：EF Core + SQLite。数据库建在用户可写目录 `%LOCALAPPDATA%`（`FileSystem.AppDataDirectory\re0agent.db`），因此装到 `Program Files` 只读目录也不影响读写。
- **LLM 接入**：OpenAI 兼容接口（`AgentLlmClient` → `OpenAiCompatibleLlmClient`），支持 SSE 流式输出、自动重试、思考 / reasoning 模式。每个智能体可独立配置端点、密钥、模型、温度、思考强度。

### 多智能体回合状态机

回合流程由 `GameProgressService` + `AgentOrchestrator` 驱动，段位编号 1–6（见 `RoundPhase.cs`）：

| 段 | 状态 | 说明 |
|----|------|------|
| — | `Idle` | 无进行中回合，显示「▶ 开始大回合」 |
| 1 | `GmOpening` | GM 开场叙事 |
| 2 | `AwaitingPlayer` | 等玩家书写主角的抉择 |
| 3 | `ProtagonistActing` | 主角行动（消费玩家输入） |
| 4 | `NpcDispatching` | 调度本回合出场的 NPC（排位号） |
| 5 | `NpcResponding` | NPC 依位号依次回应 |
| 6 | `Finalizing` | 填表 / 结算 / 存档 |
| — | `Interrupted` | 3–6 段被手动停止，等待「继续」从断点恢复 |

段位与断点编号算法化持久到 `chat_sessions.current_round_phase` / `interrupted_step`，重开 App 不靠猜。

### 主要参与者

- **GmAgent** —— 游戏主持人，负责开场与世界描述。
- **CharacterSubAgent / CharacterAgentService** —— 扮演各 NPC 角色。
- **ChapterSwitchAgent** —— 章节 / 世界状态切换。
- **FormAgent + FormAgentSqlExecutor** —— 结算阶段把剧情落地为数据库更新（经 `SqlSafetyValidator` 校验的受限 SQL）。
- **DiceEngine / CombatResolver** —— 骰子与战斗判定。
- **BlackTeaRagService** —— 世界观设定书检索。

### 界面（页面路由）

| 路由 | 页面 | 作用 |
|------|------|------|
| `/` | Home | 主游戏界面：回合推进、抉择输入、变体切换 |
| `/database` | DatabaseView | 查看当前世界 / 主角 / NPC / 物品等数据库状态 |
| `/agents` | AgentConfig（契约之书） | 配置各 LLM 智能体的端点 / 密钥 / 模型 / 参数 |
| `/world-codex` | WorldCodexView | 浏览世界观设定书 |
| `/llm-logs` | LlmLogsView | 查看 LLM 请求 / 响应日志，便于调试 |

## 安装与运行

### 方式一：安装包（推荐给玩家）

双击 `dist/install.exe`，按向导安装。安装器为 **self-contained**（已内置 .NET 8 运行时，无需额外安装任何环境），装完在开始菜单 / 桌面出现「Re:Zero 命运全书」快捷方式。

> 首次运行请到「契约之书」（`/agents`）页面填入你的 LLM API 端点与密钥（任意 OpenAI 兼容服务，如 DeepSeek / OpenAI / 本地推理服务等）。

### 方式二：从源码运行（开发者）

前置：.NET 8 SDK + MAUI 工作负载。

```bash
dotnet workload install maui-windows      # 若尚未安装
dotnet build Re0Agent.sln -c Debug
dotnet run --project src/Re0Agent.App -f net8.0-windows10.0.19041.0
```

运行测试：

```bash
dotnet test src/Re0Agent.Tests
```

## 自行打包 install.exe

打包脚本 `build/pack.ps1` 一条命令串起「PNG→ICO → self-contained 发布 → Inno Setup 编译安装器」：

```powershell
pwsh -File build/pack.ps1
# 仅重编安装器（复用已有发布产物）：
pwsh -File build/pack.ps1 -SkipPublish
```

前置依赖：

- **Inno Setup 6**（编译安装器）：`winget install JRSoftware.InnoSetup`
- **ImageMagick**（可选，用于生成多尺寸图标）：缺失时脚本自动回退到 .NET 内建的多尺寸 ICO 生成，无需第三方工具。

产物：

- `dist/app/` —— self-contained 发布文件夹（`Re0Agent.App.exe` + 运行时 + `wwwroot`）
- `dist/install.exe` —— 交付给用户的安装器

> 打包前请关闭正在运行的 App，否则 win-x64 运行时文件会被占用导致 publish 失败。

## 数据与资源

| 文件 | 说明 |
|------|------|
| `re0从零开始的异世界生活.json` | Re:Zero 世界观设定书（RAG 语料，~4.9MB） |
| `骰子表格SQL_v4.1.json` | 骰子 / 判定表格（~62KB） |
| `icon.png` | 应用图标源图（RE:0 星空主题） |

用户存档数据库位于 `%LOCALAPPDATA%` 下的 `re0agent.db`，**卸载软件不会删除**你的存档。

## 已知限制

- 目前仅提供 Windows 桌面版（项目已为 Android 迁移预留 TFM，尚未开启）。
- 安装器未做代码签名，首次运行可能出现 SmartScreen「未知发布者」提示，选择「仍要运行」即可。
- 游戏体验依赖你配置的 LLM 服务质量与上下文长度。

---

<div align="center">
<sub>「就算世界与我为敌，我也要保护你。」</sub>
</div>
