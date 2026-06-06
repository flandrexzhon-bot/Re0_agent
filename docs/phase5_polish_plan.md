# Phase 5 打磨计划：交互重构、模板系统、世界书可视化与视觉修复

## 目标
在不破坏现有 36 个测试的前提下，完成 9 项打磨：分辨率/滚动适配、清理遗留文案、世界书可视化、回合两阶段交互、死亡回归动画修复、历史回合气泡、视觉细节、代码审查修正、主角模板系统前端。

## A. 回合两阶段交互（最大改动，请重点确认）

### 现状
`AgentOrchestrator.RunRoundAsync(playerInput, skip)` 一次跑完：GM开场 → 全部角色(含主角最后) → GM总结 → 填表 → 存档。主角是 `IsPlayerControlled` 的角色Agent，已固定最后行动。

### 目标流程
1. 玩家点「回合开始」（此时无文本框）。
2. 运行 GM 开场 + 全部 NPC 回合，期间显示「角色行动中…」。
3. NPC 行动完成后，出现主角输入文本框（+「旁观/跳过」）。
4. 玩家输入并提交 → 运行主角回合 → GM总结 → 填表 → 存档。

### 实现方式（保持向后兼容，不破坏现有测试）
在 `AgentOrchestrator` 新增两个方法，保留旧 `RunRoundAsync` 不动（测试依赖它）：
- `BeginRoundAsync(skipNpcs?, ct)` → 跑 GM开场 + NPC 回合，返回一个 `GameRound`（主角回合尚未执行）。把进行中的 round 状态留给调用方。
- `CompletePlayerTurnAsync(round, playerInput, skipPlayerTurn, ct)` → 执行主角回合 + GM总结 + 填表 + 存档/死亡回归。

为支持拆分，`CharacterAgentService.LoadActiveProfilesAsync` 已返回「主角在前，NPC在后」排序——需要新增按角色类型分组的能力：`BeginRoundAsync` 只跑非主角 profile，`CompletePlayerTurnAsync` 只跑主角 profile。死亡回归检测逻辑两阶段都要保留（NPC 也可能触发对主角的致命判定，但通常死亡发生在主角回合后由 GM 总结判定）。

旧 `RunRoundAsync` 内部改为 `BeginRoundAsync` + `CompletePlayerTurnAsync` 顺序调用，保证语义不变、测试继续通过。

Home.razor 状态机：`Idle`（仅显示「回合开始」）→ `NpcRunning`（角色行动中…）→ `AwaitingPlayer`（显示输入框）→ `Finalizing`（命运编织中…）→ 回到 `Idle`。

## B. 主角模板系统前端（Tasks #6）
后端 `ProtagonistTemplateService` 已有 `ListTemplatesAsync` / `ApplyTemplateAsync` / `EnsureDefaultTemplateAsync`，但**没有任何前端入口**。
- 在 [契约之书/AgentConfig] 旁新增一个页面或在 Home 起始态加入「选择主角模板」区：列出模板（默认菜月昴高亮），点击「应用此模板」→ `ApplyTemplateAsync` → 初始化世界并创建 `initial_template` 存档。
- 起始态（数据库无 protagonist_info 时）引导玩家先选模板再开始。
- 本阶段沿用「默认+应用」深度，不做自定义模板编辑器（与 Phase 4 约定一致）。

## C. 世界书（BlackTea）可视化（Tasks #3）
后端 RAG 只有 `QueryAsync`，无「列出全部条目」能力。
- 在 `IRagService` 新增 `ListAllEntriesAsync(ct)`，`BlackTeaRagService` 复用已有 `LoadEntriesAsync` 缓存返回全部 `WorldBookEntry`。
- 在旧档案馆「世界法典」标签下新增子标签「📜 黑茶世界书（内置设定）」：按 `Comment` 分组/可搜索，展示条目 Comment、Keys、Constant、Content（折叠/展开）。
- 注意：BlackTea JSON 2.9MB，前端一次性渲染 210 条需懒加载/分页或仅展示标题+点击展开正文，避免卡顿。

## D. 死亡回归动画修复（Tasks #1 of polish）
[app.css:791-807] 的 `noise-anim`/`noise-anim-2` 用了 SCSS 语法（`$steps`、`@for`、`random()`、`percentage()`），纯 CSS 无法解析 → glitch 信号干扰特效完全失效。
- 用纯 CSS `@keyframes` 手写若干 `clip-path`/`clip` 关键帧（如 0/20/40/.../100% 各一个固定 rect），实现红黑错位抖动，无需 SCSS 编译。

## E. 历史回合气泡（Tasks #2 of polish）
现状仅当前 `lastRound` 渲染气泡+骰子卡片；历史回合塌缩为 chronicle 纯文本。
- 方案（轻量）：前端保留本 session 内已完成的多个 `GameRound`（`List<GameRound> completedRounds`），按顺序渲染气泡历史，而非只渲染最后一个。
- 数据库 chronicle 仍作为「跨 session 持久历史」展示在最上方。不改 DB schema。

## F. 分辨率/滚动适配（Tasks #1）
目标：首页整体不可上下滚动，仅中间编年史框内部滚动。
- MainLayout/app.css：`html,body` 固定 `height:100vh; overflow:hidden`；`main` 区域 flex 布局，`.fate-book` 占满高度；只有 `.grimoire-log` `overflow-y:auto`，其高度用 `flex:1` 撑满而非固定 `75vh`。
- 顶部状态栏 + 底部输入区固定，中间日志弹性滚动。

## G. 清理遗留文案（Tasks #2）
- [MainLayout.razor:10] `Re0Agent Phase 1` → 改为 `Re:Zero 命运全书` 或留空品牌名。

## H. 代码审查修正（Tasks #5）
读码时发现的问题，一并修正：
- `ProtagonistTemplateService.ResetGameAsync` 删除顺序已在上个 commit 修过外键问题，保留。
- `Home.razor` 死亡回归判定 `round.Events.Any(e => e.Contains("死亡回归完成")||e.Contains("死亡回归触发"))` 与 orchestrator 事件文案耦合，重构两阶段时同步对齐。
- 瘴气雾层 `miasmaLevel/150` 在 0 时仍渲染空层，调整为 `miasmaLevel > 0` 才显示。

## 测试与验证
- Core 单测：新增 orchestrator 两阶段方法测试（BeginRoundAsync 只跑NPC、CompletePlayerTurnAsync 跑主角并写库存档）；RAG `ListAllEntriesAsync` 返回非空；旧 `RunRoundAsync` 行为不变（现有 2 个 orchestrator 测试必须仍通过）。
- `dotnet test Re0Agent.sln --no-restore` 必须保持全绿（当前 36 个 + 新增）。
- App 构建前需确保 `Re0Agent.App.exe` 未运行（否则 DLL 被锁，build 报 MSB3027，非代码问题）。

## 改动文件清单
- Core: `Services/Agent/AgentOrchestrator.cs`、`CharacterAgentService.cs`(分组)、`Services/Settings/IRagService.cs` + `BlackTeaRagService.cs`
- App: `Components/Pages/Home.razor`(两阶段+模板起始态+历史气泡)、`DatabaseView.razor`(世界书子标签)、`Components/Layout/MainLayout.razor`、`wwwroot/css/app.css`(滚动+glitch修复)
- 可能新增: `Components/Pages/` 模板选择组件（或并入 Home）
- Tests: `Phase2AgentTests.cs` 或新增 `Phase5InteractionTests.cs`

## 假设
- 两阶段交互为本次核心，按上述「Begin/Complete」拆分实现，旧单回合 API 保留兼容。
- 世界书可视化只读展示内置 BlackTea，不做编辑。
- 不改 SQLite schema（历史气泡用前端 session 缓存，不持久化每个 turn）。
- 模板系统沿用 Phase 4「默认+应用」深度。
