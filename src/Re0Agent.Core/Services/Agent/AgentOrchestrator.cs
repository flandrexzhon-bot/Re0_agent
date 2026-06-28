using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Core.Services.Agent;

public sealed class AgentOrchestrator(
    Re0AgentDbContext dbContext,
    GmAgent gmAgent,
    CharacterSubAgent characterSubAgent,
    ChapterSwitchAgent chapterSwitchAgent,
    CharacterAgentService characterAgentService,
    FormAgent formAgent,
    FormAgentSqlExecutor sqlExecutor,
    DiceEngine diceEngine,
    CombatResolver combatResolver,
    CharacterNameResolver nameResolver,
    SaveSystem saveSystem)
{
    public async Task<GameRound> RunRoundAsync(
        string? playerInput,
        bool skipPlayerTurn,
        CancellationToken cancellationToken = default)
    {
        var round = await BeginRoundAsync(onStepCompleted: null, cancellationToken: cancellationToken);
        if (round.DeathReturnTriggered)
        {
            await FinalizeRoundAsync(round, onStepCompleted: null, cancellationToken);
            return round;
        }

        // 新顺序：主角先行动（消费玩家输入），随后 NPC 依位号回应，最后结算。
        return await RunPlayerThenNpcTurnsAsync(round, playerInput, skipPlayerTurn, directOutput: true, onStepCompleted: null, onBeforeTurn: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 第一阶段：初始化、GM开场、泉此方调度（主角排第一）。主角和NPC回合尚未执行。
    /// </summary>
    public async Task<GameRound> BeginRoundAsync(
        Func<GameRound, Task>? onStepCompleted = null,
        IReadOnlyList<GameRound>? previousRounds = null,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var round = new GameRound
        {
            RoundIndex = await CreateRoundIndexAsync(cancellationToken),
            Chapter = await ReadCurrentChapterAsync(cancellationToken),
            PreviousRounds = previousRounds ?? [],
            BaseSavePointId = await dbContext.SavePoints.AsNoTracking()
                .MaxAsync(item => (int?)item.SaveId, cancellationToken)
        };

        var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        round.PendingProtagonistProfiles = profiles.Where(profile => profile.IsPlayerControlled).ToList();

        // 新顺序：GM 先铺陈开场（不依赖位号）。主角行动后，泉此方再依据开场+主角行动调度 NPC。
        round.GmOpening = await gmAgent.CreateOpeningAsync(round, profiles, slotList: "", cancellationToken);
        round.Events.Add(round.GmOpening);

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

        return round;
    }

    /// <summary>
    /// 在场NPC按照位号顺序逐个行动。主角已在本阶段之前先行动，本方法只跑 NPC。
    /// </summary>
    public async Task RunNpcTurnsAsync(
        GameRound round,
        Func<GameRound, Task>? onStepCompleted = null,
        Func<Task>? onBeforeTurn = null,
        int skipNpcCount = 0,
        CancellationToken cancellationToken = default)
    {
        // 1. Parse slots from CharacterSub output
        var parsedSlots = new List<(string Name, int Slot, bool IsPlayer)>();
        var lines = (round.CharacterSubSlots ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // 兼容旧位号格式：含「最后行动」的行视为主角行，标记后过滤。
            if (line.Contains("最后行动", StringComparison.OrdinalIgnoreCase))
            {
                var matchProtagonist = System.Text.RegularExpressions.Regex.Match(line, @"最后行动\s*[：:\-\s]*\s*([^\r\n]+)");
                if (matchProtagonist.Success)
                {
                    var name = matchProtagonist.Groups[1].Value.Trim().Trim('*', '-', ' ', '。', '：', ':', '－', '[', ']');
                    parsedSlots.Add((name, 9999, true));
                }
                continue;
            }

            var matchSlot = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*(?:号位|号|位|\.|：|:|－|\-)\s*([^\r\n]+)");
            if (matchSlot.Success)
            {
                var slotStr = matchSlot.Groups[1].Value;
                var name = matchSlot.Groups[2].Value.Trim().Trim('*', '-', ' ', '。', '：', ':', '－', '[', ']');
                if (int.TryParse(slotStr, out var slot))
                {
                    parsedSlots.Add((name, slot, false));
                }
            }
        }

        // 泉此方 是幕后调度员，绝不应作为角色出现在位号里；防御性过滤。
        parsedSlots.RemoveAll(s => s.Name.Contains("泉此方", StringComparison.Ordinal));

        // 加载别名字典：同一角色的不同称呼（如"罗兹瓦尔"↔"罗兹瓦尔·L·梅瑟斯"）归一到规范名，
        // 避免被当成新角色重复引入。
        var aliasGroups = await nameResolver.LoadGroupsAsync(cancellationToken);

        // 3. Load active profiles from database
        var dbProfiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        var dbNpcProfiles = dbProfiles.Where(p => !p.IsPlayerControlled).ToList();

        // 新位号格式主角排在第一行（1号位），无「最后行动」标记。主角已先行动，
        // 必须把位号里的主角行剔除，避免被当作 NPC 重复行动。
        foreach (var protagonist in dbProfiles.Where(p => p.IsPlayerControlled))
        {
            parsedSlots.RemoveAll(s =>
                CharacterNameResolver.IsSameCharacter(aliasGroups, s.Name, protagonist.CharacterName));
        }

        // 4. Build NPC profiles list to run
        var npcProfilesToRun = new List<(CharacterAgentProfile Profile, int Slot)>();
        var usedDbNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in parsedSlots)
        {
            if (parsed.IsPlayer) continue;

            // 别名感知匹配：先精确，再按规范名归一比较。
            var dbMatch = dbNpcProfiles.FirstOrDefault(p =>
                    p.CharacterName.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase))
                ?? dbNpcProfiles.FirstOrDefault(p =>
                    CharacterNameResolver.IsSameCharacter(aliasGroups, p.CharacterName, parsed.Name));

            if (dbMatch is not null)
            {
                npcProfilesToRun.Add((dbMatch, parsed.Slot));
                usedDbNpcs.Add(dbMatch.CharacterName);
            }
            else
            {
                // Fallback: Create dynamic/temporary profile
                var tempProfile = new CharacterAgentProfile
                {
                    CharacterName = parsed.Name,
                    IsPlayerControlled = false,
                    WorldBookEntryKey = parsed.Name,
                    CurrentStateReference = "temporary_npc"
                };
                npcProfilesToRun.Add((tempProfile, parsed.Slot));
            }
        }

        // 位号安排是权威的：只有被角色调度Agent排进位号的NPC才出场。
        // 仅当位号里完全没有解析出任何NPC时（解析失败兜底），才回退到全部在册NPC，避免空回合。
        var hasParsedNpc = parsedSlots.Any(s => !s.IsPlayer);
        if (!hasParsedNpc)
        {
            foreach (var dbNpc in dbNpcProfiles)
            {
                if (!usedDbNpcs.Contains(dbNpc.CharacterName))
                {
                    npcProfilesToRun.Add((dbNpc, 999));
                }
            }
        }

        var sortedNpcProfiles = npcProfilesToRun.OrderBy(x => x.Slot).Select(x => x.Profile).ToList();

        // 级联重 roll：跳过前 skipNpcCount 个已保留的 NPC，只重跑其后的。
        if (skipNpcCount > 0)
        {
            sortedNpcProfiles = sortedNpcProfiles.Skip(skipNpcCount).ToList();
        }

        foreach (var profile in sortedNpcProfiles)
        {
            if (onBeforeTurn is not null)
            {
                await onBeforeTurn();
            }

            var turn = await RunCharacterTurnAsync(round, profile, playerInput: null, skip: false, directOutput: false, cancellationToken);
            if (turn.DiceResult is not null && round.DeathReturnCause is null)
            {
                round.DeathReturnCause = TryReadDeathReturnCause(turn.GmJudgement);
            }

            if (onStepCompleted is not null)
            {
                await onStepCompleted(round);
            }
            await ApplyDelayAsync(cancellationToken);

            if (round.DeathReturnCause is not null)
            {
                round.Events.Add($"死亡回归触发：{round.DeathReturnCause}");
                if (onStepCompleted is not null)
                {
                    await onStepCompleted(round);
                }
                break;
            }
        }
    }

    /// <summary>
    /// 第二阶段：主角先行动（消费玩家输入），随后泉此方调度 NPC 阵容、在场 NPC 依位号回应，
    /// 最后 GM 总结、填表写库、自动存档或死亡回归。
    /// </summary>
    public async Task<GameRound> RunPlayerThenNpcTurnsAsync(
        GameRound round,
        string? playerInput,
        bool skipPlayerTurn,
        bool directOutput = true,
        Func<GameRound, Task>? onStepCompleted = null,
        Func<Task>? onBeforeTurn = null,
        CancellationToken cancellationToken = default)
    {
        round.PlayerInput = playerInput;

        // 1. 主角先行动。
        if (round.DeathReturnCause is null)
        {
            foreach (var profile in round.PendingProtagonistProfiles)
            {
                if (onBeforeTurn is not null)
                {
                    await onBeforeTurn();
                }

                var turn = await RunCharacterTurnAsync(round, profile, playerInput, skipPlayerTurn, directOutput, cancellationToken);
                if (!turn.Skipped && turn.DiceResult is not null && round.DeathReturnCause is null)
                {
                    round.DeathReturnCause = TryReadDeathReturnCause(turn.GmJudgement);
                }

                if (onStepCompleted is not null)
                {
                    await onStepCompleted(round);
                }
                await ApplyDelayAsync(cancellationToken);
            }
        }

        round.PendingProtagonistProfiles = [];

        // 2. 主角已触发死亡回归则跳过调度与 NPC，直接结算。
        if (round.DeathReturnCause is null)
        {
            // 泉此方依据 GM 开场 + 主角已完成的行动调度本回合 NPC 阵容（不涉及主角）。
            var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
            round.CharacterSubSlots = await characterSubAgent.RunAsync(round.Chapter, profiles, round.GmOpening, round.PlayerInput, cancellationToken);

            if (onStepCompleted is not null)
            {
                await onStepCompleted(round);
            }
            await ApplyDelayAsync(cancellationToken);

            await RunNpcTurnsAsync(round, onStepCompleted, onBeforeTurn, cancellationToken: cancellationToken);
        }

        // 3. 结算。
        await FinalizeRoundAsync(round, onStepCompleted, cancellationToken);
        return round;
    }

    /// <summary>
    /// 「继续」：手动停止后恢复一个未结算的回合，保留已生成的格，从断点接着跑到回合末。
    /// 不回档数据库——沿用停止时的现状继续推进。调用方负责把 cache 与回合的格数对齐。
    /// </summary>
    public async Task<GameRound> ResumeRoundAsync(
        GameRound round,
        Func<GameRound, Task>? onStepCompleted = null,
        Func<Task>? onBeforeTurn = null,
        CancellationToken cancellationToken = default)
    {
        // RunCharacterTurnAsync 在所有 await 之后才把格加入列表，故被取消的「进行中」格不会留半成品：
        // 主角格存在 ⇒ 主角已完整行动。
        bool protagonistDone = round.CharacterTurns.Any(t => t.IsPlayerControlled);

        if (!protagonistDone)
        {
            // 主角尚未行动（停在主角格或更早）——PendingProtagonistProfiles 在取消时未被清空，
            // 直接走完整第二阶段：主角 → 调度 → NPC → 结算。
            return await RunPlayerThenNpcTurnsAsync(
                round, round.PlayerInput, skipPlayerTurn: false, directOutput: true,
                onStepCompleted, onBeforeTurn, cancellationToken);
        }

        // 主角已完成。若调度尚未产出位号（停在主角与首个 NPC 之间），补跑泉此方调度。
        if (string.IsNullOrWhiteSpace(round.CharacterSubSlots) && round.DeathReturnCause is null)
        {
            var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
            round.CharacterSubSlots = await characterSubAgent.RunAsync(round.Chapter, profiles, round.GmOpening, round.PlayerInput, cancellationToken);
            if (onStepCompleted is not null)
            {
                await onStepCompleted(round);
            }
            await ApplyDelayAsync(cancellationToken);
        }

        // 从已完成的 NPC 之后继续。已完成 NPC 数 = 非主角格数；据此 skip。
        if (round.DeathReturnCause is null)
        {
            int npcsDone = round.CharacterTurns.Count(t => !t.IsPlayerControlled);
            await RunNpcTurnsAsync(round, onStepCompleted, onBeforeTurn, skipNpcCount: npcsDone, cancellationToken: cancellationToken);
        }

        await FinalizeRoundAsync(round, onStepCompleted, cancellationToken);
        return round;
    }

    /// <summary>
    /// 级联重 roll：在已 restore 好对应 DB 快照的前提下，重跑某回合的「开场/某一格起」到回合末。
    /// <para><paramref name="keepTurnCount"/>=0 → 整局重跑（开场重 roll 或主角重 roll，由 <paramref name="regenerateOpening"/> 区分）。</para>
    /// <para><paramref name="keepTurnCount"/>≥1 → 保留前 N 格（主角 + N-1 个 NPC），从第 N 格 NPC 起级联重跑。</para>
    /// 调用方（GameProgressService）负责先 restore 快照、并在重跑前后管理变体 cache。
    /// </summary>
    public async Task<GameRound> ReRunRoundAsync(
        GameRound baseRound,
        int keepTurnCount,
        bool regenerateOpening,
        Func<GameRound, Task>? onStepCompleted = null,
        Func<Task>? onBeforeTurn = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);

        var round = new GameRound
        {
            RoundIndex = baseRound.RoundIndex,
            Chapter = baseRound.Chapter,
            PlayerInput = baseRound.PlayerInput,
            BaseSavePointId = baseRound.BaseSavePointId,
            PreviousRounds = baseRound.PreviousRounds,
            PendingProtagonistProfiles = profiles.Where(p => p.IsPlayerControlled).ToList()
        };

        // 开场：重 roll 开场则重生成，否则沿用原开场。
        round.GmOpening = regenerateOpening || string.IsNullOrWhiteSpace(baseRound.GmOpening)
            ? await gmAgent.CreateOpeningAsync(round, profiles, slotList: "", cancellationToken)
            : baseRound.GmOpening;
        round.Events.Add(round.GmOpening ?? "");

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

        if (keepTurnCount <= 0)
        {
            // 整局重跑：主角 → 泉此方调度 → NPC → 结算。
            return await RunPlayerThenNpcTurnsAsync(
                round, baseRound.PlayerInput, skipPlayerTurn: false, directOutput: true,
                onStepCompleted, onBeforeTurn, cancellationToken);
        }

        // 保留前 keepTurnCount 格（含主角），沿用原位号安排，从其后的 NPC 级联重跑。
        foreach (var kept in baseRound.CharacterTurns.Take(keepTurnCount))
        {
            round.CharacterTurns.Add(kept);
        }
        round.CharacterSubSlots = baseRound.CharacterSubSlots;
        round.PendingProtagonistProfiles = [];

        // turns[0] 为主角，NPC 从 turns[1] 起；保留 keepTurnCount 格意味着已保留 keepTurnCount-1 个 NPC。
        await RunNpcTurnsAsync(round, onStepCompleted, onBeforeTurn, skipNpcCount: keepTurnCount - 1, cancellationToken: cancellationToken);
        await FinalizeRoundAsync(round, onStepCompleted, cancellationToken);
        return round;
    }

    private async Task<CharacterTurn> RunCharacterTurnAsync(
        GameRound round,
        CharacterAgentProfile profile,
        string? playerInput,
        bool skip,
        bool directOutput,
        CancellationToken cancellationToken)
    {
        var turn = new CharacterTurn
        {
            RoundIndex = round.RoundIndex,
            OrderNumber = round.CharacterTurns.Count + 1,
            CharacterName = profile.CharacterName,
            IsPlayerControlled = profile.IsPlayerControlled,
            PlayerInstruction = profile.IsPlayerControlled ? playerInput : null,
            Skipped = profile.IsPlayerControlled && skip
        };

        if (turn.Skipped)
        {
            turn.ActionText = "玩家选择跳过本回合。";
            turn.GmJudgement = "判定：无。";
            turn.DiceResult = new DiceResult { Command = "无", Outcome = "跳过" };
            turn.ResultResponse = "角色保持旁观。";
        }
        else
        {
            if (profile.IsPlayerControlled && directOutput && !string.IsNullOrWhiteSpace(playerInput))
            {
                // 直接输出：玩家文本原样作为主角行动，不经过角色 agent 加工。
                turn.ActionText = playerInput;
            }
            else
            {
                turn.ActionText = await characterAgentService.RunTurnAsync(
                    round,
                    profile,
                    profile.IsPlayerControlled ? playerInput : null,
                    cancellationToken);
            }

            turn.GmJudgement = await gmAgent.JudgeTurnAsync(round, turn, cancellationToken);
            turn.DiceResult = await diceEngine.ExecuteAsync(turn.GmJudgement, cancellationToken);
            turn.DiceCommand = turn.DiceResult.Command;

            // 战斗结算：按角色 ID 直写 hp/mp/stamina，不经过填表 Agent。
            if (turn.DiceResult.IsCombat)
            {
                var combat = await combatResolver.ApplyAsync(turn.DiceResult, cancellationToken);
                if (combat.DefenderName is not null && combat.RemainingHp is not null)
                {
                    round.Events.Add($"战斗结算：{combat.DefenderName} 剩余生命值 {combat.RemainingHp}。");
                }

                // 主角生命归零 → 触发死亡回归。
                if (combat.ProtagonistDied && round.DeathReturnCause is null)
                {
                    round.DeathReturnCause = $"{combat.DefenderName}在战斗中生命值归零。";
                }
            }

            turn.ResultResponse = $"{profile.CharacterName}接受判定：{FormatDiceResult(turn.DiceResult)}。";
        }

        round.CharacterTurns.Add(turn);
        round.Events.Add($"{turn.OrderNumber}. {turn.CharacterName}: {turn.ActionText}");
        round.Events.Add($"骰子：{FormatDiceResult(turn.DiceResult)}");
        return turn;
    }

    private async Task FinalizeRoundAsync(
        GameRound round,
        Func<GameRound, Task>? onStepCompleted,
        CancellationToken cancellationToken)
    {
        // 并发运行章节切换 Agent 与填表 Agent。
        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

        // 章节切换：启动但不等待 —— 填表 SQL 不依赖章节切换结果。
        var chapterSwitchTask = chapterSwitchAgent.RunAsync(round, cancellationToken);

        int maxFormRetries = 3;
        int formAttempt = 0;
        bool formSuccess = false;
        SqlExecutionResult? formExecution = null;

        while (formAttempt < maxFormRetries && !formSuccess)
        {
            formAttempt++;
            try
            {
                var sql = await formAgent.GenerateSqlAsync(round, cancellationToken);
                formExecution = await sqlExecutor.ExecuteAsync(sql, cancellationToken);
                formSuccess = true;
            }
            catch (Exception ex)
            {
                round.Events.Add($"填表Agent填表失败第 {formAttempt} 次，原因：{ex.Message}");
                if (formAttempt >= maxFormRetries)
                {
                    round.Events.Add($"填表Agent填表最终失败。可稍后在【现世处境】手动重试。");
                }
            }
        }

        if (formSuccess && formExecution is not null)
        {
            round.Events.Add($"填表Agent执行SQL：{formExecution.StatementsExecuted}条。");
        }

        // 等待章节切换完成并应用结果。
        try
        {
            var newChapter = await chapterSwitchTask;
            if (newChapter is not null)
            {
                var globalState = await dbContext.GlobalStates.FirstOrDefaultAsync(cancellationToken);
                if (globalState is not null && globalState.CurrentChapter != newChapter.Value)
                {
                    globalState.CurrentChapter = newChapter.Value;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    round.Chapter = newChapter.Value;
                    round.Events.Add($"帕秋莉判断需要切换章节：第 {newChapter.Value} 章。");
                }
                else
                {
                    round.Events.Add($"帕秋莉判断无需切换章节（当前第{round.Chapter}章）。");
                }
            }
            else
            {
                round.Events.Add($"帕秋莉判断无需切换章节（当前第{round.Chapter}章）。");
            }
        }
        catch (Exception ex)
        {
            round.Events.Add($"章节切换Agent异常：{ex.Message}");
        }

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

        var latestChronicleIndex = await ReadLatestChronicleIndexAsync(cancellationToken);
        if (round.DeathReturnCause is not null)
        {
            var deathReturn = await saveSystem.TriggerDeathReturnAsync(
                round.DeathReturnCause,
                latestChronicleIndex,
                cancellationToken: cancellationToken);
            round.Events.Add($"死亡回归完成：恢复存档#{deathReturn.SavePointId}，第{deathReturn.LoopCount}次循环，瘴气={deathReturn.MiasmaLevel}。");
        }
        else
        {
            var savePoint = await saveSystem.CreateSavePointAsync("round_end", cancellationToken);
            round.Events.Add($"自动存档：#{savePoint.SaveId}，章节{savePoint.Chapter}。");
        }

        round.CompletedAt = DateTimeOffset.UtcNow;

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
    }

    private async Task ApplyDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            var delayStr = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "Delay")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);

            if (double.TryParse(delayStr, out var seconds) && seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }
        catch
        {
            // Ignore if routing table doesn't exist yet (e.g. initial setup)
        }
    }

    private async Task<string> CreateRoundIndexAsync(CancellationToken cancellationToken)
    {
        var next = await dbContext.Chronicle.CountAsync(cancellationToken) + 1;
        return $"R{next:0000}";
    }

    private async Task<int> ReadCurrentChapterAsync(CancellationToken cancellationToken)
    {
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return state?.CurrentChapter ?? 1;
    }

    private async Task<string?> ReadLatestChronicleIndexAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Chronicle.AsNoTracking()
            .OrderByDescending(entry => entry.RowId)
            .Select(entry => entry.CodeIndex)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? TryReadDeathReturnCause(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            const string fullWidthMarker = "死亡回归：";
            const string halfWidthMarker = "死亡回归:";
            if (line.StartsWith(fullWidthMarker, StringComparison.Ordinal))
            {
                return ReadCause(line[fullWidthMarker.Length..]);
            }

            if (line.StartsWith(halfWidthMarker, StringComparison.Ordinal))
            {
                return ReadCause(line[halfWidthMarker.Length..]);
            }
        }

        return null;
    }

    private static string ReadCause(string value)
    {
        var cause = value.Trim();
        return string.IsNullOrWhiteSpace(cause) ? "未说明死因" : cause;
    }

    private static string FormatDiceResult(DiceResult? result)
    {
        if (result is null)
        {
            return "未执行";
        }

        var outcomeText = result.Outcome == result.SuccessLevel
            ? result.Outcome
            : $"{result.Outcome}/{result.SuccessLevel}";

        var rollText = result.Roll is null
            ? string.Empty
            : $"，骰值{result.Roll}/{result.TargetAfterModifiers ?? result.Target}";
        var detail = string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $"，{result.Detail}";
        return $"{result.Command} => {outcomeText}{rollText}{detail}";
    }

}
