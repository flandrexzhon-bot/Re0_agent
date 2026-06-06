using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class AgentOrchestrator(
    Re0AgentDbContext dbContext,
    GmAgent gmAgent,
    CharacterAgentService characterAgentService,
    FormAgent formAgent,
    FormAgentSqlExecutor sqlExecutor)
{
    public async Task<GameRound> RunRoundAsync(
        string? playerInput,
        bool skipPlayerTurn,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var round = new GameRound
        {
            RoundIndex = await CreateRoundIndexAsync(cancellationToken),
            Chapter = await ReadCurrentChapterAsync(cancellationToken),
            PlayerInput = playerInput
        };

        var profiles = await characterAgentService.LoadActiveProfilesAsync(cancellationToken);
        round.GmOpening = await gmAgent.CreateOpeningAsync(round, profiles, cancellationToken);
        round.Events.Add(round.GmOpening);

        var order = 1;
        foreach (var profile in profiles)
        {
            var turn = new CharacterTurn
            {
                RoundIndex = round.RoundIndex,
                OrderNumber = order++,
                CharacterName = profile.CharacterName,
                IsPlayerControlled = profile.IsPlayerControlled,
                PlayerInstruction = profile.IsPlayerControlled ? playerInput : null,
                Skipped = profile.IsPlayerControlled && skipPlayerTurn
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
                turn.DiceCommand = ReadDiceCommand(turn.GmJudgement);
                turn.DiceResult = CreatePhaseTwoDiceResult(turn.DiceCommand);
                turn.ResultResponse = $"{profile.CharacterName}接受判定并完成本回合回应。";
            }

            round.CharacterTurns.Add(turn);
            round.Events.Add($"{turn.OrderNumber}. {turn.CharacterName}: {turn.ActionText}");
        }

        round.GmSummary = await gmAgent.SummarizeAsync(round, cancellationToken);
        round.Events.Add(round.GmSummary);

        var sql = await formAgent.GenerateSqlAsync(round, cancellationToken);
        var execution = await sqlExecutor.ExecuteAsync(sql, cancellationToken);
        round.Events.Add($"填表Agent执行SQL：{execution.StatementsExecuted}条。");
        round.CompletedAt = DateTimeOffset.UtcNow;

        return round;
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

    private static string ReadDiceCommand(string? judgement)
    {
        if (string.IsNullOrWhiteSpace(judgement))
        {
            return "无";
        }

        if (judgement.Contains("必成", StringComparison.Ordinal))
        {
            return "必成";
        }

        if (judgement.Contains("必败", StringComparison.Ordinal))
        {
            return "必败";
        }

        return "无";
    }

    private static DiceResult CreatePhaseTwoDiceResult(string command)
    {
        return command switch
        {
            "必成" => new DiceResult { Command = command, Outcome = "成功", Detail = "Phase2占位判定" },
            "必败" => new DiceResult { Command = command, Outcome = "失败", Detail = "Phase2占位判定" },
            _ => new DiceResult { Command = "无", Outcome = "无需检定", Detail = "完整骰子系统留到Phase3" }
        };
    }
}
