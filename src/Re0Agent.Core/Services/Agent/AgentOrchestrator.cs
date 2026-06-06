using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Core.Services.Agent;

public sealed class AgentOrchestrator(
    Re0AgentDbContext dbContext,
    GmAgent gmAgent,
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
        var round = await BeginRoundAsync(cancellationToken);
        if (round.DeathReturnTriggered)
        {
            await FinalizeRoundAsync(round, cancellationToken);
            return round;
        }

        return await CompletePlayerTurnAsync(round, playerInput, skipPlayerTurn, cancellationToken);
    }

    /// <summary>
    /// 第一阶段：初始化、GM开场、在场NPC逐个行动。主角回合尚未执行。
    /// </summary>
    public async Task<GameRound> BeginRoundAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var round = new GameRound
        {
            RoundIndex = await CreateRoundIndexAsync(cancellationToken),
            Chapter = await ReadCurrentChapterAsync(cancellationToken)
        };

        var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        round.PendingProtagonistProfiles = profiles.Where(profile => profile.IsPlayerControlled).ToList();

        round.GmOpening = await gmAgent.CreateOpeningAsync(round, profiles, cancellationToken);
        round.Events.Add(round.GmOpening);

        foreach (var profile in profiles.Where(profile => !profile.IsPlayerControlled))
        {
            var turn = await RunCharacterTurnAsync(round, profile, playerInput: null, skip: false, cancellationToken);
            if (turn.DiceResult is not null && round.DeathReturnCause is null)
            {
                round.DeathReturnCause = TryReadDeathReturnCause(turn.GmJudgement);
            }

            if (round.DeathReturnCause is not null)
            {
                round.Events.Add($"死亡回归触发：{round.DeathReturnCause}");
                break;
            }
        }

        return round;
    }

    /// <summary>
    /// 第二阶段：执行主角回合，随后 GM 总结、填表写库、自动存档或死亡回归。
    /// </summary>
    public async Task<GameRound> CompletePlayerTurnAsync(
        GameRound round,
        string? playerInput,
        bool skipPlayerTurn,
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
            }
        }

        round.PendingProtagonistProfiles = [];
        await FinalizeRoundAsync(round, cancellationToken);
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

    private async Task FinalizeRoundAsync(GameRound round, CancellationToken cancellationToken)
    {
        round.GmSummary = await gmAgent.SummarizeAsync(round, cancellationToken);
        round.Events.Add(round.GmSummary);
        round.DeathReturnCause ??= TryReadDeathReturnCause(round.GmSummary);

        var sql = await formAgent.GenerateSqlAsync(round, cancellationToken);
        var execution = await sqlExecutor.ExecuteAsync(sql, cancellationToken);
        round.Events.Add($"填表Agent执行SQL：{execution.StatementsExecuted}条。");

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

        var rollText = result.Roll is null
            ? string.Empty
            : $"，骰值{result.Roll}/{result.TargetAfterModifiers ?? result.Target}";
        var detail = string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $"，{result.Detail}";
        return $"{result.Command} => {result.Outcome}/{result.SuccessLevel}{rollText}{detail}";
    }
}
