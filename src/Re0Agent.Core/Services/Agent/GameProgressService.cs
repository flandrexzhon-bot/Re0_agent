using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class GameProgressService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public event Action? OnStateChanged;

    public RoundPhase Phase { get; private set; } = RoundPhase.Idle;
    public bool IsBusy { get; private set; }
    public GameRound? ActiveRound { get; private set; }
    public List<GameRound> SessionRounds { get; } = new();

    private CancellationTokenSource? _roundCts;

    // ── 重 roll / 变体 cache（纯内存，仅当前回合存活；开新大回合时整批释放）──
    /// <summary>当前回合起点（GM 开场前）的 DB 快照。整局重跑 restore 到此。</summary>
    private SavePoint? _roundStartSnapshot;
    /// <summary>当前回合「每格开始前」的 DB 快照列表（含主角格）。逐格重 roll restore 到对应项。</summary>
    private readonly List<SavePoint> _preTurnSnapshots = new();
    /// <summary>当前回合的整回合变体列表（#0 为原版）。</summary>
    private readonly List<RoundVariant> _currentRoundVariants = new();
    private int _activeVariantIndex;

    /// <summary>当前回合已记录的变体数量（含原版）。</summary>
    public int CurrentVariantCount => _currentRoundVariants.Count;
    /// <summary>当前激活的变体序号（0 基）。</summary>
    public int ActiveVariantIndex => _activeVariantIndex;
    /// <summary>是否可对当前（最新已结算）回合重 roll：存在原版变体且回合已结算、且空闲。</summary>
    public bool CanReRoll => !IsBusy && _currentRoundVariants.Count > 0 && _roundStartSnapshot is not null;

    /// <summary>请求中止当前正在运行的大回合（GM/NPC/结算阶段）。用于调试。</summary>
    public void StopRound()
    {
        _roundCts?.Cancel();
    }

    public List<ChatSession> ChatSessions { get; private set; } = new();
    public int ActiveSessionId { get; private set; }
    public string NewSessionName { get; set; } = string.Empty;

    /// <summary>
    /// 每个会话未提交的输入栏草稿（按 SessionId 索引）。存在单例服务里，
    /// 这样切到别的栏目/页面再回来、或切换会话，草稿都不丢。
    /// </summary>
    private readonly Dictionary<int, string> _playerInputDrafts = new();

    public string GetPlayerInputDraft(int sessionId)
        => _playerInputDrafts.GetValueOrDefault(sessionId, string.Empty);

    public void SavePlayerInputDraft(int sessionId, string? draft)
        => _playerInputDrafts[sessionId] = draft ?? string.Empty;

    public int CurrentChapter { get; private set; } = 1;
    public int LoopCount { get; private set; } = 0;
    public int MiasmaLevel { get; private set; } = 0;
    public bool ShowDeathGlitch { get; private set; } = false;
    public bool HasProtagonist { get; private set; } = false;
    public List<ChronicleEntry> Chronicles { get; private set; } = new();
    public IReadOnlyList<ProtagonistTemplate> Templates { get; private set; } = new List<ProtagonistTemplate>();

    public ProtagonistInfo? Protagonist { get; private set; }
    public List<InventoryItem> Inventories { get; private set; } = new();
    public List<EquipmentItem> Equipments { get; private set; } = new();
    public List<Quest> Quests { get; private set; } = new();
    public string? LocationDescription { get; private set; }
    public string? LocationRegion { get; private set; }

    public List<ImportantNpc> AllNpcs { get; private set; } = new();
    public List<string> AllCharacterNames { get; private set; } = new();
    public List<Re0Agent.Core.Entities.AgentConfig> AgentConfigs { get; private set; } = new();

    public string SelectedGmPreset { get; set; } = string.Empty;
    public string SelectedNpcPreset { get; set; } = string.Empty;
    public string SelectedDicePreset { get; set; } = string.Empty;
    public string SelectedMemoryPreset { get; set; } = string.Empty;
    public string SelectedCharacterSubPreset { get; set; } = string.Empty;
    public string SelectedChapterSwitchPreset { get; set; } = string.Empty;
    public double DelaySeconds { get; set; } = 0.0;

    /// <summary>全局自动重试开关：输出为空或报错时自动重复请求（最多 3 次）。</summary>
    public bool AutoRetryEnabled { get; set; } = true;

    /// <summary>静态全局暴露，供 AgentLlmClient 等无需 DI 的底层读取。</summary>
    public static bool AutoRetryGlobal { get; private set; } = true;

    /// <summary>全局流式传输开关：开启后底层 LLM 调用走 SSE 流式接口。</summary>
    public bool StreamingEnabled { get; set; } = false;

    /// <summary>静态全局暴露，供 AgentLlmClient 等无需 DI 的底层读取。</summary>
    public static bool StreamingGlobal { get; private set; } = false;

    public List<CharacterBinding> CharacterBindings { get; private set; } = new();
    public List<TempNpcBinding> TempNpcBindings { get; private set; } = new();

    // Wizard fields
    public bool IsInitializingGame { get; set; }
    public bool IsStartingAdventure { get; set; }
    public int InitStep { get; set; } = 1;
    public bool IsPrologueStage { get; set; }
    public string CustomOpeningMessage { get; set; } = string.Empty;
    public bool IncludeSubaruAsNpc { get; set; } = true;
    
    public ProtagonistInfo CustomProtagonist { get; set; } = new()
    {
        Name = "",
        Gender = "男",
        Age = 17,
        Appearance = "黑发眼眸，身着运动服",
        IdentityText = "被召唤至异世界的少年",
        SelfStatus = "正常",
        LocationName = "王都",
        BaseAttributes = "体质:45; 敏捷:55; 感知:60; 意志:70",
        SpecialAttributes = "死亡回归:特殊",
        ResourcesText = "手机; 方便面; 薯片"
    };

    public bool IsCustomProtagonist { get; set; }
    public ProtagonistTemplate? SelectedTemplate { get; set; }
    public ProtagonistInfo? SelectedTemplateProtagonist { get; set; }
    public int SelectedTemplateId { get; set; }
    public int SelectedChapter { get; set; } = 1;
    public int AutoSelectedChapter { get; set; } = 1;

    public List<ChapterInfo> AvailableChapters { get; } = new()
    {
        new(1, "开始的结束 (王都的一日)", "露格尼卡王国的王都，一切命运的起点，与银发半精灵美少女的邂逅与轮回。"),
        new(7, "自觉的感情 (宅邸的一周)", "罗兹瓦尔宅邸，平静日常生活下的暗流，诅咒、魔兽与接踵而至的绝望循环。"),
        new(18, "再访王都", "王选之局开启，与爱蜜莉雅产生裂痕，面对白鲸与魔女教的疯狂袭击。"),
        new(53, "千辛万苦抵达的地方 (圣域的试炼与强欲魔女)", "神秘的圣域与古老墓地，魔女的茶会，多重地狱般的因果纠缠与誓言的抉择。"),
        new(82, "开头总由来访者开始 (水门都市的抗战)", "受邀造访水门都市普利斯特拉，数个大罪司教同时突袭，前所未有的都市防卫战打响。")
    };

    public class CharacterBinding
    {
        public string CharacterName { get; set; } = string.Empty;
        public string PresetName { get; set; } = string.Empty;
    }

    public class TempNpcBinding
    {
        public string NpcName { get; set; } = string.Empty;
        public string PresetName { get; set; } = string.Empty;
    }

    public class ChapterInfo
    {
        public int Number { get; }
        public string Title { get; }
        public string Desc { get; }

        public ChapterInfo(int number, string title, string desc)
        {
            Number = number;
            Title = title;
            Desc = desc;
        }
    }

    public GameProgressService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }

    public void ClearErrorMessage()
    {
        ErrorMessage = null;
        NotifyStateChanged();
    }

    public string? ErrorMessage { get; private set; }

    public async Task LoadDatabaseStateAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Re0AgentDbContext>();
        var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();
        var templateService = scope.ServiceProvider.GetRequiredService<ProtagonistTemplateService>();

        try
        {
            // 首次启动时数据库可能尚未建表（建表原本只在开始回合时触发），先确保表结构就绪。
            await DatabaseInitializer.InitializeAsync(db, cancellationToken);

            var state = await db.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            CurrentChapter = state?.CurrentChapter ?? 1;

            ChatSessions = (await sessionService.ListSessionsAsync(cancellationToken)).ToList();
            var active = ChatSessions.FirstOrDefault(s => s.IsActive == 1);
            ActiveSessionId = active?.SessionId ?? 0;

            if (active is not null)
            {
                try
                {
                    var rounds = JsonSerializer.Deserialize<List<GameRound>>(active.DetailedRoundsSnapshot, JsonOptions);
                    if (rounds is not null)
                    {
                        SessionRounds.Clear();
                        SessionRounds.AddRange(rounds);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            HasProtagonist = await db.ProtagonistInfo.AsNoTracking().AnyAsync(cancellationToken);
            Templates = await templateService.ListTemplatesAsync(cancellationToken);

            if (SessionRounds.Count > 0)
            {
                IsPrologueStage = false;
                var lastRound = SessionRounds.Last();
                if (lastRound.CompletedAt == null)
                {
                    ActiveRound = lastRound;
                    if (Phase == RoundPhase.Idle)
                    {
                        Phase = RoundPhase.AwaitingPlayer;
                    }
                }
                else
                {
                    ActiveRound = null;
                    Phase = RoundPhase.Idle;
                    // 应用重启 / 切会话后内存重 roll cache 为空——为最新已结算回合
                    // 从持久化存档锚点重建最小 cache，使 swipe/↻ 按钮重新可用。
                    await TryReconstructRerollCacheAsync(db, lastRound, cancellationToken);
                }
            }
            else
            {
                ActiveRound = null;
                Phase = RoundPhase.Idle;
            }
            bool isChatEmpty = SessionRounds.Count == 0 && !IsPrologueStage;
            if ((!HasProtagonist || isChatEmpty) && !IsInitializingGame && !IsStartingAdventure)
            {
                IsInitializingGame = true;
                InitStep = 1;
            }

            var latestLog = await db.DeathReturnLog.AsNoTracking()
                .OrderByDescending(item => item.LogId)
                .FirstOrDefaultAsync(cancellationToken);
            LoopCount = latestLog?.LoopCount ?? 0;
            MiasmaLevel = latestLog?.MiasmaLevel ?? 0;

            Chronicles = await db.Chronicle.AsNoTracking()
                .OrderBy(c => c.RowId)
                .ToListAsync(cancellationToken);

            Protagonist = await db.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (Protagonist is not null)
            {
                Inventories = await db.Inventory.AsNoTracking().ToListAsync(cancellationToken);
                Equipments = await db.Equipment.AsNoTracking().ToListAsync(cancellationToken);
                var loc = await db.WorldMapPoints.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.LocationName == Protagonist.LocationName, cancellationToken);
                if (loc is not null)
                {
                    LocationDescription = loc.EnvironmentDesc;
                    LocationRegion = $"{loc.MajorRegion} · {loc.MinorRegion}";
                }
                else
                {
                    LocationDescription = "未知的异世界区域";
                    LocationRegion = "未知领域";
                }
            }
            else
            {
                Inventories.Clear();
                Equipments.Clear();
                LocationDescription = null;
                LocationRegion = null;
            }

            // Load presets, npcs, routings
            AllNpcs = await db.ImportantNpcs.AsNoTracking().OrderBy(n => n.RowId).ToListAsync(cancellationToken);
            AllCharacterNames = await db.ImportantNpcs.AsNoTracking().Select(n => n.Name).ToListAsync(cancellationToken);
            Quests = await db.Quests.AsNoTracking().OrderBy(q => q.QuestName).ToListAsync(cancellationToken);
            AgentConfigs = await db.AgentConfig.AsNoTracking().Where(c => c.Enabled == 1).ToListAsync(cancellationToken);

            var routings = await db.ApiRoutings.ToListAsync(cancellationToken);
            SelectedGmPreset = routings.FirstOrDefault(r => r.RoutingKey == "GM")?.PresetName ?? string.Empty;
            SelectedNpcPreset = routings.FirstOrDefault(r => r.RoutingKey == "NPC")?.PresetName ?? string.Empty;
            SelectedDicePreset = routings.FirstOrDefault(r => r.RoutingKey == "Dice")?.PresetName ?? string.Empty;
            SelectedMemoryPreset = routings.FirstOrDefault(r => r.RoutingKey == "Memory")?.PresetName ?? string.Empty;
            SelectedCharacterSubPreset = routings.FirstOrDefault(r => r.RoutingKey == "CharacterSub")?.PresetName ?? string.Empty;
            SelectedChapterSwitchPreset = routings.FirstOrDefault(r => r.RoutingKey == "ChapterSwitch")?.PresetName ?? string.Empty;

            var delayStr = routings.FirstOrDefault(r => r.RoutingKey == "Delay")?.PresetName;
            if (double.TryParse(delayStr, out var dVal))
            {
                DelaySeconds = dVal;
            }
            else
            {
                DelaySeconds = 0.0;
            }

            var autoRetryStr = routings.FirstOrDefault(r => r.RoutingKey == "AutoRetry")?.PresetName;
            AutoRetryEnabled = !string.Equals(autoRetryStr, "0", StringComparison.Ordinal);
            AutoRetryGlobal = AutoRetryEnabled;

            var streamingStr = routings.FirstOrDefault(r => r.RoutingKey == "Streaming")?.PresetName;
            StreamingEnabled = string.Equals(streamingStr, "1", StringComparison.Ordinal);
            StreamingGlobal = StreamingEnabled;

            CharacterBindings = routings
                .Where(r => r.RoutingKey.StartsWith("Character_"))
                .Select(r => new CharacterBinding
                {
                    CharacterName = r.RoutingKey.Substring("Character_".Length),
                    PresetName = r.PresetName
                })
                .ToList();

            TempNpcBindings = routings
                .Where(r => r.RoutingKey.StartsWith("TempNpc_"))
                .Select(r => new TempNpcBinding
                {
                    NpcName = r.RoutingKey.Substring("TempNpc_".Length),
                    PresetName = r.PresetName
                })
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载状态错误: {ex.Message}";
        }
    }

    /// <summary>
    /// 应用重启 / 切会话后内存重 roll cache 丢失。为最新已结算回合从持久化存档锚点
    /// 重建最小 cache：起点快照 = 本回合 BaseSavePointId（上一回合末存档），变体 #0 末态
    /// = 最新持久化存档。逐格快照无法从持久化重建（只有 round_end 存档），故留空——
    /// 此时逐格 ↻ 会回退到回合起点重跑（见 ReRollAsync），整局 ↻ 仍精确。
    /// 首回合无起点基线（BaseSavePointId=null）时不重建，保持重 roll 禁用。
    /// </summary>
    private async Task TryReconstructRerollCacheAsync(Re0AgentDbContext db, GameRound lastRound, CancellationToken cancellationToken)
    {
        // 已有内存 cache（本会话内刚跑过）则不覆盖。
        if (_currentRoundVariants.Count > 0 && _roundStartSnapshot is not null)
        {
            return;
        }

        _currentRoundVariants.Clear();
        _preTurnSnapshots.Clear();
        _activeVariantIndex = 0;
        _roundStartSnapshot = null;

        try
        {
            // 末态快照：最新持久化存档（FinalizeRoundAsync 的 round_end）。
            var endSnapshot = await db.SavePoints.AsNoTracking()
                .OrderByDescending(sp => sp.SaveId)
                .FirstOrDefaultAsync(cancellationToken);
            if (endSnapshot is null)
            {
                return;
            }

            // 起点快照：本回合 BaseSavePointId 指向的存档（上一回合末）。
            // 拿不到真正的回合起点基线时不重建——用末态当起点会在重跑时把填表叠加到
            // 已结算状态上，宁可禁用重 roll 也不污染数据库。首回合(BaseSavePointId=null)即此情形。
            if (lastRound.BaseSavePointId is not int baseId)
            {
                return;
            }
            var startSnapshot = await db.SavePoints.AsNoTracking()
                .FirstOrDefaultAsync(sp => sp.SaveId == baseId, cancellationToken);
            if (startSnapshot is null)
            {
                return;
            }
            _roundStartSnapshot = startSnapshot;

            _currentRoundVariants.Add(new RoundVariant
            {
                GmOpening = lastRound.GmOpening,
                Turns = lastRound.CharacterTurns.ToList(),
                Events = lastRound.Events.ToList(),
                DbSnapshot = endSnapshot
            });
            _activeVariantIndex = 0;
        }
        catch
        {
            // 重建失败不致命——仅意味着该回合暂不可重 roll。
            _currentRoundVariants.Clear();
            _roundStartSnapshot = null;
        }
    }

    public async Task UpdateChapterAsync(int newChapter, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Re0AgentDbContext>();
        try
        {
            var state = await db.GlobalStates.FirstOrDefaultAsync(cancellationToken);
            if (state is not null)
            {
                state.CurrentChapter = newChapter;
                await db.SaveChangesAsync(cancellationToken);
                
                // Add a system event log
                var latestRound = SessionRounds.LastOrDefault();
                if (latestRound is not null)
                {
                    latestRound.Events.Add($"玩家手动调整故事线：章节切换为第 {newChapter} 章");
                    var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();
                    var json = JsonSerializer.Serialize(SessionRounds, JsonOptions);
                    await sessionService.SaveActiveSessionStateAsync(json, cancellationToken);
                }
                
                await LoadDatabaseStateAsync(cancellationToken);
                NotifyStateChanged();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"手动修改章节失败: {ex.Message}";
            NotifyStateChanged();
        }
    }

    public async Task AutoSaveChatSessionAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();
        try
        {
            var json = JsonSerializer.Serialize(SessionRounds, JsonOptions);
            await sessionService.SaveActiveSessionStateAsync(json, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Autosave error: {ex.Message}");
        }
    }

    public Task BeginRoundAsync()
    {
        if (IsBusy) return Task.CompletedTask;

        IsBusy = true;
        Phase = RoundPhase.GmRunning;
        ErrorMessage = null;
        _roundCts = new CancellationTokenSource();
        var token = _roundCts.Token;
        NotifyStateChanged();

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
                var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();

                // 释放上一回合的重 roll cache，并抓本回合起点快照（GM 开场前）。
                _currentRoundVariants.Clear();
                _preTurnSnapshots.Clear();
                _activeVariantIndex = 0;
                _roundStartSnapshot = await saveSystem.CaptureInMemorySnapshotAsync(token);

                // 1. GM Opening
                IReadOnlyList<GameRound> prevRounds;
                lock (SessionRounds)
                    prevRounds = SessionRounds.TakeLast(2).ToList();

                var round = await orchestrator.BeginRoundAsync(onStepCompleted: async (r) =>
                {
                    ActiveRound = r;
                    lock (SessionRounds)
                    {
                        var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == r.RoundIndex);
                        if (idx >= 0) SessionRounds[idx] = r;
                        else SessionRounds.Add(r);
                    }
                    await AutoSaveChatSessionAsync();
                    NotifyStateChanged();
                    await Task.Delay(10);
                }, previousRounds: prevRounds, cancellationToken: token);

                ActiveRound = round;
                lock (SessionRounds)
                {
                    var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == round.RoundIndex);
                    if (idx >= 0) SessionRounds[idx] = round;
                    else SessionRounds.Add(round);
                }
                await AutoSaveChatSessionAsync();
                IsPrologueStage = false;
                NotifyStateChanged();

                if (round.DeathReturnTriggered)
                {
                    await HandleRoundCompletionAsync(round);
                    return;
                }

                // 2. 等待玩家输入 —— 主角将先行动，NPC 随后在提交阶段响应。
                Phase = RoundPhase.AwaitingPlayer;
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = "已手动中止本回合。";
                RollbackActiveRound();
                Phase = RoundPhase.Idle;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                RollbackActiveRound();
                Phase = RoundPhase.Idle;
            }
            finally
            {
                _roundCts?.Dispose();
                _roundCts = null;
                IsBusy = false;
                NotifyStateChanged();
                await LoadDatabaseStateAsync();
                NotifyStateChanged();
            }
        });
        return Task.CompletedTask;
    }

    public Task SubmitPlayerTurnAsync(string playerInput, bool skipPlayerTurn, bool directOutput = true)
    {
        if (IsBusy || ActiveRound is null)
        {
            Phase = RoundPhase.Idle;
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        IsBusy = true;
        // 主角先行动、NPC 随后响应期间显示「角色响应中」；结算阶段切到 Finalizing。
        Phase = RoundPhase.NpcRunning;
        ErrorMessage = null;
        _roundCts = new CancellationTokenSource();
        var token = _roundCts.Token;
        NotifyStateChanged();

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
            var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();
            try
            {
                var round = await orchestrator.RunPlayerThenNpcTurnsAsync(ActiveRound, playerInput, skipPlayerTurn, directOutput, onStepCompleted: async (r) =>
                {
                    ActiveRound = r;
                    lock (SessionRounds)
                    {
                        var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == r.RoundIndex);
                        if (idx >= 0) SessionRounds[idx] = r;
                    }
                    await AutoSaveChatSessionAsync();
                    NotifyStateChanged();
                    await Task.Delay(10);
                }, onBeforeTurn: async () =>
                {
                    // 每格开始前抓快照，供逐格重 roll 回档。
                    _preTurnSnapshots.Add(await saveSystem.CaptureInMemorySnapshotAsync(token));
                }, cancellationToken: token);

                // 记录原版变体（#0）：结算后的回合内容 + 末态快照。
                await CaptureVariantAsync(round, saveSystem, token);

                await HandleRoundCompletionAsync(round);
            }
            catch (OperationCanceledException)
            {
                // 手动停止：回合未结算但 GM 开场与（部分）格已生成。进入 Interrupted，
                // 保留已生成内容 + 抓当前 DB 状态为变体 #0，使「继续」可恢复、各格可重 roll。
                ErrorMessage = "已手动停止本回合，可点「继续」恢复或对某格重 roll。";
                if (ActiveRound is not null)
                {
                    try { await CaptureVariantAsync(ActiveRound, saveSystem, CancellationToken.None); } catch { /* 抓变体失败不致命 */ }
                    await AutoSaveChatSessionAsync();
                }
                Phase = RoundPhase.Interrupted;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Phase = RoundPhase.AwaitingPlayer;
            }
            finally
            {
                _roundCts?.Dispose();
                _roundCts = null;
                IsBusy = false;
                NotifyStateChanged();
                await LoadDatabaseStateAsync();
                NotifyStateChanged();
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>「继续」：从手动停止处恢复未结算回合，保留已生成的格、接着跑到回合末。</summary>
    public Task ResumeRoundAsync()
    {
        if (IsBusy || Phase != RoundPhase.Interrupted || ActiveRound is null)
        {
            return Task.CompletedTask;
        }

        IsBusy = true;
        Phase = RoundPhase.NpcRunning;
        ErrorMessage = null;
        _roundCts = new CancellationTokenSource();
        var token = _roundCts.Token;
        var resumeRound = ActiveRound;
        NotifyStateChanged();

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
            var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();
            try
            {
                // 停止时已抓过变体 #0；恢复要继续写库，先丢弃它，跑完后重新打包为权威 #0。
                _currentRoundVariants.Clear();
                _activeVariantIndex = 0;

                // 把「每格前快照」对齐到已完成的格数——丢弃停止时为半成品格抓的多余快照，
                // 否则恢复后续格 onBeforeTurn 追加的快照会与 CharacterTurns 错位，逐格重 roll 索引偏移。
                int doneTurns;
                lock (SessionRounds) { doneTurns = resumeRound.CharacterTurns.Count; }
                if (_preTurnSnapshots.Count > doneTurns)
                {
                    _preTurnSnapshots.RemoveRange(doneTurns, _preTurnSnapshots.Count - doneTurns);
                }

                var round = await orchestrator.ResumeRoundAsync(resumeRound, onStepCompleted: async (r) =>
                {
                    ActiveRound = r;
                    lock (SessionRounds)
                    {
                        var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == r.RoundIndex);
                        if (idx >= 0) SessionRounds[idx] = r;
                    }
                    await AutoSaveChatSessionAsync();
                    NotifyStateChanged();
                    await Task.Delay(10);
                }, onBeforeTurn: async () =>
                {
                    _preTurnSnapshots.Add(await saveSystem.CaptureInMemorySnapshotAsync(token));
                }, cancellationToken: token);

                await CaptureVariantAsync(round, saveSystem, token);
                await HandleRoundCompletionAsync(round);
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = "已再次停止本回合，可点「继续」恢复或对某格重 roll。";
                if (ActiveRound is not null)
                {
                    try { await CaptureVariantAsync(ActiveRound, saveSystem, CancellationToken.None); } catch { }
                    await AutoSaveChatSessionAsync();
                }
                Phase = RoundPhase.Interrupted;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Phase = RoundPhase.Interrupted;
            }
            finally
            {
                _roundCts?.Dispose();
                _roundCts = null;
                IsBusy = false;
                NotifyStateChanged();
                await LoadDatabaseStateAsync();
                NotifyStateChanged();
            }
        });
        return Task.CompletedTask;
    }

    private async Task HandleRoundCompletionAsync(GameRound round)
    {
        if (round.DeathReturnTriggered)
        {
            ShowDeathGlitch = true;
            NotifyStateChanged();
            await Task.Delay(1800);
            ShowDeathGlitch = false;
            NotifyStateChanged();
        }

        ActiveRound = null;
        Phase = RoundPhase.Idle;
        await LoadDatabaseStateAsync();
        await AutoSaveChatSessionAsync();
        NotifyStateChanged();
    }

    private void RollbackActiveRound()
    {
        if (ActiveRound is not null)
        {
            lock (SessionRounds)
            {
                SessionRounds.Remove(ActiveRound);
            }
            ActiveRound = null;
        }
    }

    /// <summary>把一个已结算回合的内容 + 当前 DB 末态快照打包成变体，追加到当前回合变体列表并设为激活。</summary>
    private async Task CaptureVariantAsync(GameRound round, SaveSystem saveSystem, CancellationToken token)
    {
        var snapshot = await saveSystem.CaptureInMemorySnapshotAsync(token);
        _currentRoundVariants.Add(new RoundVariant
        {
            GmOpening = round.GmOpening,
            Turns = round.CharacterTurns.ToList(),
            Events = round.Events.ToList(),
            DbSnapshot = snapshot
        });
        _activeVariantIndex = _currentRoundVariants.Count - 1;
    }

    /// <summary>
    /// 级联重 roll 当前（最新已结算）回合。
    /// <para><paramref name="fromTurnIndex"/> = -1：重 roll GM 开场并整局重跑。</para>
    /// <para><paramref name="fromTurnIndex"/> = 0：保留开场，从主角格起重跑（重抽判定/骰子 + 级联 NPC）。</para>
    /// <para><paramref name="fromTurnIndex"/> ≥ 1：保留开场 + 前 fromTurnIndex 格，从该 NPC 格起级联重跑。</para>
    /// </summary>
    public Task ReRollAsync(int fromTurnIndex, bool regenerateOpening)
    {
        if (IsBusy || _roundStartSnapshot is null) return Task.CompletedTask;

        GameRound? baseRound;
        lock (SessionRounds)
        {
            baseRound = SessionRounds.LastOrDefault();
        }
        if (baseRound is null) return Task.CompletedTask;

        IsBusy = true;
        Phase = fromTurnIndex < 0 ? RoundPhase.GmRunning : RoundPhase.NpcRunning;
        ErrorMessage = null;
        _roundCts = new CancellationTokenSource();
        var token = _roundCts.Token;
        NotifyStateChanged();

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
                var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();

                // keepTurnCount：保留多少格（含主角）。fromTurnIndex<0 或 0 → 整局重跑(0)；≥1 → 保留 fromTurnIndex 格。
                int keepTurnCount = fromTurnIndex <= 0 ? 0 : fromTurnIndex;

                // 逐格重 roll 需要对应的「每格前快照」才能精确回档；若 cache 缺失该项
                // （如应用重启后从持久化重建、只有回合起点快照），降级为整局重跑——
                // 否则会出现「保留前 N 格、却把 DB 全量回退到回合起点」的不一致。
                if (keepTurnCount > 0 && _preTurnSnapshots.Count <= keepTurnCount)
                {
                    keepTurnCount = 0;
                    regenerateOpening = false; // 保留原开场，仅重跑主角+NPC。
                }

                // 1. 回档到对应快照。
                var restoreFrom = keepTurnCount == 0
                    ? _roundStartSnapshot
                    : _preTurnSnapshots[keepTurnCount];
                await saveSystem.RestoreGameStateAsync(restoreFrom!, token);

                // 2. 从该点向后级联重跑。重跑期间继续抓「每格前快照」覆盖式更新 cache。
                var newPreTurnSnapshots = _preTurnSnapshots.Take(keepTurnCount).ToList();

                var round = await orchestrator.ReRunRoundAsync(
                    baseRound, keepTurnCount, regenerateOpening,
                    onStepCompleted: async (r) =>
                    {
                        ActiveRound = r;
                        lock (SessionRounds)
                        {
                            var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == r.RoundIndex);
                            if (idx >= 0) SessionRounds[idx] = r;
                        }
                        NotifyStateChanged();
                        await Task.Delay(10);
                    },
                    onBeforeTurn: async () =>
                    {
                        newPreTurnSnapshots.Add(await saveSystem.CaptureInMemorySnapshotAsync(token));
                    },
                    cancellationToken: token);

                // 3. 更新 cache + 记录新变体。
                _preTurnSnapshots.Clear();
                _preTurnSnapshots.AddRange(newPreTurnSnapshots);

                lock (SessionRounds)
                {
                    var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == round.RoundIndex);
                    if (idx >= 0) SessionRounds[idx] = round;
                }
                ActiveRound = null;
                await CaptureVariantAsync(round, saveSystem, token);

                Phase = RoundPhase.Idle;
                await LoadDatabaseStateAsync();
                await AutoSaveChatSessionAsync();
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = "已中止重 roll。";
                Phase = RoundPhase.Idle;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"重 roll 失败: {ex.Message}";
                Phase = RoundPhase.Idle;
            }
            finally
            {
                _roundCts?.Dispose();
                _roundCts = null;
                IsBusy = false;
                NotifyStateChanged();
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>在已记录的变体之间切换：restore 该变体的 DB 末态快照，并把其叙事回填到会话。</summary>
    public async Task SelectVariant(int index)
    {
        if (IsBusy || index < 0 || index >= _currentRoundVariants.Count || index == _activeVariantIndex)
        {
            return;
        }

        var variant = _currentRoundVariants[index];
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();
            await saveSystem.RestoreGameStateAsync(variant.DbSnapshot, CancellationToken.None);

            lock (SessionRounds)
            {
                var last = SessionRounds.LastOrDefault();
                if (last is not null)
                {
                    last.GmOpening = variant.GmOpening;
                    last.CharacterTurns = variant.Turns.ToList();
                    last.Events = variant.Events.ToList();
                }
            }
            _activeVariantIndex = index;
            await LoadDatabaseStateAsync();
            await AutoSaveChatSessionAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"切换变体失败: {ex.Message}";
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// fork「引入叙事」：从指定回合的 BaseSavePointId 回溯世界状态（不计死亡/瘴气），
    /// 并把会话裁剪到该回合之前。
    /// </summary>
    public async Task ForkAtAsync(GameRound round)
    {
        if (IsBusy || round.BaseSavePointId is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var saveSystem = scope.ServiceProvider.GetRequiredService<SaveSystem>();
            await saveSystem.ForkRestoreAsync(round.BaseSavePointId.Value);

            lock (SessionRounds)
            {
                var idx = SessionRounds.FindIndex(sr => sr.RoundIndex == round.RoundIndex);
                if (idx >= 0)
                {
                    SessionRounds.RemoveRange(idx, SessionRounds.Count - idx);
                }
            }

            // fork 后当前回合 cache 失效。
            _currentRoundVariants.Clear();
            _preTurnSnapshots.Clear();
            _roundStartSnapshot = null;
            _activeVariantIndex = 0;

            ActiveRound = null;
            Phase = RoundPhase.Idle;
            // 先把裁剪后的会话落盘，再刷新数据库面板状态——否则 LoadDatabaseStateAsync
            // 会用尚未更新的旧快照覆盖 SessionRounds，导致裁剪被撤销、fork 看起来没反应。
            await AutoSaveChatSessionAsync();
            await LoadDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"fork 引入叙事失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task SwitchSessionAsync(int targetSessionId)
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();

            var currentJson = JsonSerializer.Serialize(SessionRounds, JsonOptions);
            var targetJson = await sessionService.SwitchSessionAsync(targetSessionId, currentJson);

            SessionRounds.Clear();
            var rounds = JsonSerializer.Deserialize<List<GameRound>>(targetJson, JsonOptions);
            if (rounds is not null)
            {
                SessionRounds.AddRange(rounds);
            }
            ActiveRound = null;
            Phase = RoundPhase.Idle;
            IsPrologueStage = false;

            await LoadDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"切换会话失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task CreateSessionAsync(string name)
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();

            var emptyRounds = await sessionService.CreateNewSessionAsync(name);
            SessionRounds.Clear();
            ActiveRound = null;
            Phase = RoundPhase.Idle;
            IsPrologueStage = false;

            await LoadDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"新建会话失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task DeleteCurrentSessionAsync(int sessionId)
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ChatSessionService>();

            await sessionService.DeleteSessionAsync(sessionId);
            SessionRounds.Clear();
            ActiveRound = null;
            Phase = RoundPhase.Idle;
            IsPrologueStage = false;

            await LoadDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"删除会话失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task ApplyTemplateAsync(int templateId)
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var templateService = scope.ServiceProvider.GetRequiredService<ProtagonistTemplateService>();

            await templateService.ApplyTemplateAsync(templateId);
            SessionRounds.Clear();
            ActiveRound = null;
            Phase = RoundPhase.Idle;
            IsPrologueStage = false;

            await LoadDatabaseStateAsync();
            await AutoSaveChatSessionAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"应用主角模板失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task SaveRoutingConfigAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Re0AgentDbContext>();

            var existing = await db.ApiRoutings.ToListAsync();
            db.ApiRoutings.RemoveRange(existing);

            var newRoutings = new List<ApiRouting>();
            if (!string.IsNullOrWhiteSpace(SelectedGmPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "GM", PresetName = SelectedGmPreset });
            if (!string.IsNullOrWhiteSpace(SelectedNpcPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "NPC", PresetName = SelectedNpcPreset });
            if (!string.IsNullOrWhiteSpace(SelectedDicePreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "Dice", PresetName = SelectedDicePreset });
            
            newRoutings.Add(new ApiRouting { RoutingKey = "Delay", PresetName = DelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });

            newRoutings.Add(new ApiRouting { RoutingKey = "AutoRetry", PresetName = AutoRetryEnabled ? "1" : "0" });
            AutoRetryGlobal = AutoRetryEnabled;

            newRoutings.Add(new ApiRouting { RoutingKey = "Streaming", PresetName = StreamingEnabled ? "1" : "0" });
            StreamingGlobal = StreamingEnabled;

            if (!string.IsNullOrWhiteSpace(SelectedMemoryPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "Memory", PresetName = SelectedMemoryPreset });

            if (!string.IsNullOrWhiteSpace(SelectedCharacterSubPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "CharacterSub", PresetName = SelectedCharacterSubPreset });

            if (!string.IsNullOrWhiteSpace(SelectedChapterSwitchPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "ChapterSwitch", PresetName = SelectedChapterSwitchPreset });

            foreach (var b in CharacterBindings)
            {
                if (!string.IsNullOrWhiteSpace(b.CharacterName) && !string.IsNullOrWhiteSpace(b.PresetName))
                {
                    newRoutings.Add(new ApiRouting { RoutingKey = "Character_" + b.CharacterName.Trim(), PresetName = b.PresetName });
                }
            }

            foreach (var b in TempNpcBindings)
            {
                if (!string.IsNullOrWhiteSpace(b.NpcName) && !string.IsNullOrWhiteSpace(b.PresetName))
                {
                    newRoutings.Add(new ApiRouting { RoutingKey = "TempNpc_" + b.NpcName.Trim(), PresetName = b.PresetName });
                }
            }

            db.ApiRoutings.AddRange(newRoutings);
            await db.SaveChangesAsync();

            await LoadDatabaseStateAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存配置契约失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task EmbarkOnAdventureAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Re0AgentDbContext>();
            var templateService = scope.ServiceProvider.GetRequiredService<ProtagonistTemplateService>();

            if (IsCustomProtagonist)
            {
                var templateName = $"自定义主角：{CustomProtagonist.Name}";
                var existing = await db.ProtagonistTemplates
                    .FirstOrDefaultAsync(t => t.TemplateName == templateName);
                if (existing is not null)
                {
                    db.ProtagonistTemplates.Remove(existing);
                    await db.SaveChangesAsync();
                }

                int includesSubaruInt = IncludeSubaruAsNpc ? 1 : 0;
                var cleanProtagonist = new ProtagonistInfo
                {
                    RowId = 1,
                    Name = CustomProtagonist.Name,
                    Gender = CustomProtagonist.Gender,
                    Age = CustomProtagonist.Age,
                    Appearance = CustomProtagonist.Appearance,
                    IdentityText = CustomProtagonist.IdentityText,
                    SelfStatus = CustomProtagonist.SelfStatus ?? "正常",
                    LocationName = CustomProtagonist.LocationName ?? "王都",
                    BaseAttributes = CustomProtagonist.BaseAttributes,
                    SpecialAttributes = CustomProtagonist.SpecialAttributes,
                    ResourcesText = CustomProtagonist.ResourcesText
                };

                var baseData = JsonSerializer.Serialize(new { protagonist = cleanProtagonist }, JsonOptions);
                var newTpl = new ProtagonistTemplate
                {
                    TemplateName = templateName,
                    IncludesSubaru = includesSubaruInt,
                    BaseData = baseData,
                    IsDefault = 0
                };
                db.ProtagonistTemplates.Add(newTpl);
                await db.SaveChangesAsync();

                await templateService.ApplyTemplateAsync(newTpl.TemplateId, SelectedChapter);
            }
            else
            {
                await templateService.ApplyTemplateAsync(SelectedTemplateId, SelectedChapter);
            }

            if (!string.IsNullOrWhiteSpace(CustomOpeningMessage))
            {
                var prologueText = CustomOpeningMessage.Trim();
                if (prologueText.Length < 200)
                {
                    prologueText = prologueText + "\n" + new string(' ', 200 - prologueText.Length);
                }
                else if (prologueText.Length > 2000)
                {
                    prologueText = prologueText.Substring(0, 2000);
                }

                var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                var prologueEntry = new ChronicleEntry
                {
                    CodeIndex = "AM0000",
                    TimeSpan = $"{nowStr} ~ {nowStr}",
                    Summary = "开场事件",
                    ChronicleText = prologueText
                };
                db.Chronicle.Add(prologueEntry);
                await db.SaveChangesAsync();
            }

            CustomOpeningMessage = string.Empty;
            SessionRounds.Clear();
            ActiveRound = null;
            Phase = RoundPhase.Idle;
            IsInitializingGame = false;
            IsStartingAdventure = false;
            IsPrologueStage = true;

            await LoadDatabaseStateAsync();
            await AutoSaveChatSessionAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"开启冒险失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public void StartNewGameWizard()
    {
        IsInitializingGame = true;
        InitStep = 1;
        IsCustomProtagonist = false;
        SelectedTemplate = null;
        SelectedTemplateId = 0;
        CustomOpeningMessage = string.Empty;
        CustomProtagonist = new()
        {
            Name = "",
            Gender = "男",
            Age = 17,
            Appearance = "黑发眼眸，身着运动服",
            IdentityText = "被召唤至异世界的少年",
            SelfStatus = "正常",
            LocationName = "王都",
            BaseAttributes = "体质:45; 敏捷:55; 感知:60; 意志:70",
            SpecialAttributes = "死亡回归:特殊",
            ResourcesText = "手机; 方便面; 薯片"
        };
        IncludeSubaruAsNpc = true;
        NotifyStateChanged();
    }

    public void GoToStep1()
    {
        InitStep = 1;
        NotifyStateChanged();
    }

    public void GoToStep2()
    {
        InitStep = 2;
        NotifyStateChanged();
    }

    public void GoToStep3()
    {
        var location = IsCustomProtagonist ? CustomProtagonist.LocationName : (SelectedTemplateProtagonist?.LocationName ?? "王都");
        if (location.Contains("宅邸") || location.Contains("罗兹瓦尔"))
        {
            AutoSelectedChapter = 7;
        }
        else if (location.Contains("圣域") || location.Contains("克莱恩"))
        {
            AutoSelectedChapter = 53;
        }
        else if (location.Contains("水门") || location.Contains("普利斯特拉"))
        {
            AutoSelectedChapter = 82;
        }
        else
        {
            AutoSelectedChapter = 1;
        }
        SelectedChapter = AutoSelectedChapter;
        InitStep = 3;
        NotifyStateChanged();
    }

    public void GoToStep4()
    {
        InitStep = 4;
        NotifyStateChanged();
    }

    public void SelectTemplate(ProtagonistTemplate tpl)
    {
        SelectedTemplate = tpl;
        SelectedTemplateId = tpl.TemplateId;
        IsCustomProtagonist = false;
        try
        {
            SelectedTemplateProtagonist = ReadTemplateProtagonist(tpl.BaseData);
        }
        catch
        {
            SelectedTemplateProtagonist = null;
        }
        NotifyStateChanged();
    }

    public void ToggleCustomProtagonist(bool custom)
    {
        IsCustomProtagonist = custom;
        NotifyStateChanged();
    }

    private ProtagonistInfo ReadTemplateProtagonist(string baseData)
    {
        var doc = JsonDocument.Parse(baseData);
        var pEl = doc.RootElement.GetProperty("protagonist");
        return new ProtagonistInfo
        {
            Name = pEl.GetProperty("Name").GetString() ?? "",
            Gender = pEl.GetProperty("Gender").GetString() ?? "",
            Age = pEl.GetProperty("Age").GetInt32(),
            Appearance = pEl.GetProperty("Appearance").GetString() ?? "",
            IdentityText = pEl.GetProperty("IdentityText").GetString() ?? "",
            SelfStatus = pEl.GetProperty("SelfStatus").GetString() ?? "正常",
            LocationName = pEl.GetProperty("LocationName").GetString() ?? "王都",
            BaseAttributes = pEl.GetProperty("BaseAttributes").GetString() ?? "",
            SpecialAttributes = pEl.GetProperty("SpecialAttributes").GetString() ?? "",
            ResourcesText = pEl.GetProperty("ResourcesText").GetString() ?? ""
        };
    }

    public bool IsValidProtagonist()
    {
        if (IsCustomProtagonist)
        {
            return !string.IsNullOrWhiteSpace(CustomProtagonist.Name) &&
                   !string.IsNullOrWhiteSpace(CustomProtagonist.LocationName) &&
                   !string.IsNullOrWhiteSpace(CustomProtagonist.BaseAttributes);
        }
        return SelectedTemplate != null;
    }

    public void AddCharacterBinding()
    {
        CharacterBindings.Add(new CharacterBinding());
        NotifyStateChanged();
    }

    public void RemoveCharacterBinding(CharacterBinding b)
    {
        CharacterBindings.Remove(b);
        NotifyStateChanged();
    }

    public void AddTempNpcBinding()
    {
        TempNpcBindings.Add(new TempNpcBinding());
        NotifyStateChanged();
    }

    public void RemoveTempNpcBinding(TempNpcBinding b)
    {
        TempNpcBindings.Remove(b);
        NotifyStateChanged();
    }
}
