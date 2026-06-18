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

    /// <summary>请求中止当前正在运行的大回合（GM/NPC/结算阶段）。用于调试。</summary>
    public void StopRound()
    {
        _roundCts?.Cancel();
    }

    public List<ChatSession> ChatSessions { get; private set; } = new();
    public int ActiveSessionId { get; private set; }
    public string NewSessionName { get; set; } = string.Empty;

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
    public double DelaySeconds { get; set; } = 0.0;

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
            AgentConfigs = await db.AgentConfig.AsNoTracking().Where(c => c.Enabled == 1).ToListAsync(cancellationToken);

            var routings = await db.ApiRoutings.ToListAsync(cancellationToken);
            SelectedGmPreset = routings.FirstOrDefault(r => r.RoutingKey == "GM")?.PresetName ?? string.Empty;
            SelectedNpcPreset = routings.FirstOrDefault(r => r.RoutingKey == "NPC")?.PresetName ?? string.Empty;
            SelectedDicePreset = routings.FirstOrDefault(r => r.RoutingKey == "Dice")?.PresetName ?? string.Empty;
            SelectedMemoryPreset = routings.FirstOrDefault(r => r.RoutingKey == "Memory")?.PresetName ?? string.Empty;
            SelectedCharacterSubPreset = routings.FirstOrDefault(r => r.RoutingKey == "CharacterSub")?.PresetName ?? string.Empty;

            var delayStr = routings.FirstOrDefault(r => r.RoutingKey == "Delay")?.PresetName;
            if (double.TryParse(delayStr, out var dVal))
            {
                DelaySeconds = dVal;
            }
            else
            {
                DelaySeconds = 0.0;
            }

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

                // 1. GM Opening
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
                }, cancellationToken: token);

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

                // 2. NPC Turns
                Phase = RoundPhase.NpcRunning;
                NotifyStateChanged();

                await orchestrator.RunNpcTurnsAsync(round, onStepCompleted: async (r) =>
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
                }, cancellationToken: token);

                if (round.DeathReturnTriggered)
                {
                    await HandleRoundCompletionAsync(round);
                    return;
                }

                // 3. Awaiting Player
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
        Phase = RoundPhase.Finalizing;
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

                var round = await orchestrator.CompletePlayerTurnAsync(ActiveRound, playerInput, skipPlayerTurn, directOutput, onStepCompleted: async (r) =>
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
                }, cancellationToken: token);

                await HandleRoundCompletionAsync(round);
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = "已手动中止本回合结算。";
                Phase = RoundPhase.AwaitingPlayer;
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

            if (!string.IsNullOrWhiteSpace(SelectedMemoryPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "Memory", PresetName = SelectedMemoryPreset });

            if (!string.IsNullOrWhiteSpace(SelectedCharacterSubPreset))
                newRoutings.Add(new ApiRouting { RoutingKey = "CharacterSub", PresetName = SelectedCharacterSubPreset });

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
                else if (prologueText.Length > 600)
                {
                    prologueText = prologueText.Substring(0, 600);
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
