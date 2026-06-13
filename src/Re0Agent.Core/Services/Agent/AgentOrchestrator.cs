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
    CharacterAgentService characterAgentService,
    FormAgent formAgent,
    FormAgentSqlExecutor sqlExecutor,
    DiceEngine diceEngine,
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

        await RunNpcTurnsAsync(round, onStepCompleted: null, cancellationToken: cancellationToken);
        if (round.DeathReturnTriggered)
        {
            await FinalizeRoundAsync(round, onStepCompleted: null, cancellationToken);
            return round;
        }

        return await CompletePlayerTurnAsync(round, playerInput, skipPlayerTurn, onStepCompleted: null, cancellationToken);
    }

    /// <summary>
    /// 第一阶段：初始化、GM开场。主角和NPC回合尚未执行。
    /// </summary>
    public async Task<GameRound> BeginRoundAsync(
        Func<GameRound, Task>? onStepCompleted = null,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var round = new GameRound
        {
            RoundIndex = await CreateRoundIndexAsync(cancellationToken),
            Chapter = await ReadCurrentChapterAsync(cancellationToken)
        };

        var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        round.PendingProtagonistProfiles = profiles.Where(profile => profile.IsPlayerControlled).ToList();

        round.CharacterSubSlots = await characterSubAgent.RunAsync(round.Chapter, profiles, cancellationToken);
        round.GmOpening = await gmAgent.CreateOpeningAsync(round, profiles, round.CharacterSubSlots, cancellationToken);
        round.Events.Add(round.GmOpening);

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

        return round;
    }

    /// <summary>
    /// 第二阶段：在场NPC按照位号顺序逐个行动。
    /// </summary>
    public async Task RunNpcTurnsAsync(
        GameRound round,
        Func<GameRound, Task>? onStepCompleted = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Parse slots from CharacterSub output
        var parsedSlots = new List<(string Name, int Slot, bool IsPlayer)>();
        var lines = (round.CharacterSubSlots ?? round.GmOpening ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
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

        // 2. Determine current location from protagonist for NPC insertion
        var currentLocation = "王都";
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (protagonist is not null)
        {
            currentLocation = protagonist.LocationName;
        }

        // 3. Ensure all parsed NPCs exist in the database and are marked "在场"
        foreach (var parsed in parsedSlots)
        {
            if (parsed.IsPlayer) continue;
            await EnsureNpcExistsAsync(parsed.Name, currentLocation, cancellationToken);
        }

        // 4. Load active profiles from database (now including any newly created ones)
        var dbProfiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        var dbNpcProfiles = dbProfiles.Where(p => !p.IsPlayerControlled).ToList();

        // 5. Build NPC profiles list to run
        var npcProfilesToRun = new List<(CharacterAgentProfile Profile, int Slot)>();
        var usedDbNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in parsedSlots)
        {
            if (parsed.IsPlayer) continue;

            // Find matching db profile
            var dbMatch = dbNpcProfiles.FirstOrDefault(p => p.CharacterName.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase));
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

        foreach (var profile in sortedNpcProfiles)
        {
            var turn = await RunCharacterTurnAsync(round, profile, playerInput: null, skip: false, cancellationToken);
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
    /// 第二阶段：执行主角回合，随后 GM 总结、填表写库、自动存档或死亡回归。
    /// </summary>
    public async Task<GameRound> CompletePlayerTurnAsync(
        GameRound round,
        string? playerInput,
        bool skipPlayerTurn,
        Func<GameRound, Task>? onStepCompleted = null,
        CancellationToken cancellationToken = default)
    {
        round.PlayerInput = playerInput;

        if (round.DeathReturnCause is null)
        {
            foreach (var profile in round.PendingProtagonistProfiles)
            {
                var turn = await RunCharacterTurnAsync(round, profile, playerInput, skipPlayerTurn, cancellationToken);
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
        await FinalizeRoundAsync(round, onStepCompleted, cancellationToken);
        return round;
    }

    private async Task<CharacterTurn> RunCharacterTurnAsync(
        GameRound round,
        CharacterAgentProfile profile,
        string? playerInput,
        bool skip,
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
            turn.ActionText = await characterAgentService.RunTurnAsync(
                round,
                profile,
                profile.IsPlayerControlled ? playerInput : null,
                cancellationToken);

            turn.GmJudgement = await gmAgent.JudgeTurnAsync(round, turn, cancellationToken);
            turn.DiceResult = await diceEngine.ExecuteAsync(turn.GmJudgement, cancellationToken);
            turn.DiceCommand = turn.DiceResult.Command;
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
        round.GmSummary = await gmAgent.SummarizeAsync(round, cancellationToken);
        round.Events.Add(round.GmSummary);
        round.DeathReturnCause ??= TryReadDeathReturnCause(round.GmSummary);

        // Detect chapter switching from EJS/GM
        await DetectAndApplyChapterChangesAsync(round, cancellationToken);

        if (onStepCompleted is not null)
        {
            await onStepCompleted(round);
        }
        await ApplyDelayAsync(cancellationToken);

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

    private async Task DetectAndApplyChapterChangesAsync(
        GameRound round,
        CancellationToken cancellationToken)
    {
        var texts = new List<string?> { round.GmOpening, round.GmSummary };
        foreach (var turn in round.CharacterTurns)
        {
            texts.Add(turn.ActionText);
            texts.Add(turn.ResultResponse);
            texts.Add(turn.GmJudgement);
        }

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var match = System.Text.RegularExpressions.Regex.Match(text, @"_\.set\(\s*['""]chapter['""]\s*,\s*(\d+)\s*\)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var newChapter))
            {
                var globalState = await dbContext.GlobalStates.FirstOrDefaultAsync(cancellationToken);
                if (globalState is not null && globalState.CurrentChapter != newChapter)
                {
                    globalState.CurrentChapter = newChapter;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    round.Chapter = newChapter;
                    round.Events.Add($"[EJS / GM 章节切换] 检测到章节变更指令，当前章节已切换为：第 {newChapter} 章");
                    break;
                }
            }
        }
    }

    private async Task EnsureNpcExistsAsync(string npcName, string currentLocation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;

        var existing = await dbContext.ImportantNpcs
            .FirstOrDefaultAsync(n => n.Name.ToLower() == npcName.ToLower(), cancellationToken);

        if (existing is null)
        {
            var newNpc = new ImportantNpc
            {
                Name = npcName,
                Gender = "未知",
                Age = 18,
                BriefIntro = "由GM剧情引入的角色",
                Appearance = "由GM剧情引入的角色",
                IdentityText = "由GM剧情引入的角色",
                BaseAttributes = "体质:50; 敏捷:50; 感知:50; 意志:50",
                LocationName = currentLocation,
                RelationsText = "暂无详细记录",
                InteractionOptions = "交谈; 观察; 离开",
                PastExperience = "暂无详细记录",
                SelfStatus = "正常"
            };
            dbContext.ImportantNpcs.Add(newNpc);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (string.IsNullOrWhiteSpace(existing.InteractionOptions) || existing.InteractionOptions == "无")
        {
            existing.InteractionOptions = "交谈; 观察; 离开";
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
