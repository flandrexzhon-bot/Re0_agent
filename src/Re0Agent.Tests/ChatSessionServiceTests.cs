using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;
using Xunit;

namespace Re0Agent.Tests;

public sealed class ChatSessionServiceTests
{
    [Fact]
    public async Task ChatSessionServiceEnsuresDefaultSessionAndAutoSavesAndSwitches()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var diceEngine = new DiceEngine(new DiceCommandParser(), new CharacterAttributeProvider(context), new SequenceDiceRoller([50]));
            var saveSystem = new SaveSystem(context, diceEngine);
            var templateService = new ProtagonistTemplateService(context, saveSystem);
            var sessionService = new ChatSessionService(context, templateService);

            // 1. EnsureDefaultSessionAsync initializes database and seeds first session
            await sessionService.EnsureDefaultSessionAsync();

            var sessions = await sessionService.ListSessionsAsync();
            Assert.Single(sessions);
            Assert.Equal("默认会话", sessions[0].SessionName);
            Assert.Equal(1, sessions[0].IsActive);

            // Verify a protagonist has been set up via the default template Subaru
            var hasProtagonist = await context.ProtagonistInfo.AnyAsync();
            Assert.True(hasProtagonist);

            // Add some mock data to sandbox tables
            var protagonist = await context.ProtagonistInfo.SingleAsync();
            protagonist.SelfStatus = "精神饱满";
            await context.SaveChangesAsync();

            // 2. SaveActiveSessionStateAsync saves current detailed rounds and sandbox snap
            const string mockRounds = "[{\"RoundIndex\":\"R0001\",\"GmSummary\":\"场景引入\",\"Events\":[],\"PlayerInput\":\"你好\"}]";
            await sessionService.SaveActiveSessionStateAsync(mockRounds);

            // Re-fetch session to verify snapshot matches modified protagonist status
            var activeSession = await sessionService.GetActiveSessionAsync();
            Assert.NotNull(activeSession);
            Assert.Equal(mockRounds, activeSession.DetailedRoundsSnapshot);
            Assert.Contains("精神饱满", activeSession.ProtagonistSnapshot);

            // 3. CreateNewSessionAsync saves current, clears sandbox, applies default template, creates new session
            var newRoundsJson = await sessionService.CreateNewSessionAsync("艾蜜莉雅线");
            Assert.Equal("[]", newRoundsJson);

            // Verify sessions list has 2 entries now
            sessions = await sessionService.ListSessionsAsync();
            Assert.Equal(2, sessions.Count);
            Assert.Equal("艾蜜莉雅线", sessions[0].SessionName); // ordered by descending ID
            Assert.Equal(1, sessions[0].IsActive);
            Assert.Equal(0, sessions[1].IsActive);

            // Verify the previous active session saved the rounds and modified state
            Assert.Equal(mockRounds, sessions[1].DetailedRoundsSnapshot);
            Assert.Contains("精神饱满", sessions[1].ProtagonistSnapshot);

            // Verify the new active session protagonist status was reset to default template
            var currentProtagonist = await context.ProtagonistInfo.SingleAsync();
            Assert.Equal("正常", currentProtagonist.SelfStatus);

            // 4. SwitchSessionAsync saves current, clears sandbox, restores target session snapshots
            currentProtagonist.SelfStatus = "略显疲惫";
            await context.SaveChangesAsync();

            var (restoredRounds, _, _, _) = await sessionService.SwitchSessionAsync(sessions[1].SessionId, "[]", null, null, null);
            Assert.Equal(mockRounds, restoredRounds);

            sessions = await sessionService.ListSessionsAsync();
            Assert.Equal(1, sessions[1].IsActive);
            Assert.Equal(0, sessions[0].IsActive);

            // Verify that switched back session restored the protagonist status "精神饱满"
            var restoredProtagonist = await context.ProtagonistInfo.SingleAsync();
            Assert.Equal("精神饱满", restoredProtagonist.SelfStatus);

            // Verify that the other session saved its modified status "略显疲惫" in its snapshot
            var otherSession = sessions.First(s => s.SessionName == "艾蜜莉雅线");
            Assert.Contains("略显疲惫", otherSession.ProtagonistSnapshot);

            // 5. DeleteSessionAsync switches active session and deletes the target
            // Currently sessions[1] (默认会话) is active. Delete it.
            await sessionService.DeleteSessionAsync(sessions[1].SessionId);

            sessions = await sessionService.ListSessionsAsync();
            Assert.Single(sessions);
            Assert.Equal("艾蜜莉雅线", sessions[0].SessionName);
            Assert.Equal(1, sessions[0].IsActive);

            // Verify we cannot delete the last remaining session
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessionService.DeleteSessionAsync(sessions[0].SessionId));
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task SaveActiveSessionPersistsPhaseAndInterruptedStepAcrossReload()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            // 1. 第一个 context：在「段5（NPC 回应）」处停止落库（Interrupted + step=5）。
            await using (var context = CreateContext(databasePath))
            {
                var diceEngine = new DiceEngine(new DiceCommandParser(), new CharacterAttributeProvider(context), new SequenceDiceRoller([50]));
                var saveSystem = new SaveSystem(context, diceEngine);
                var templateService = new ProtagonistTemplateService(context, saveSystem);
                var sessionService = new ChatSessionService(context, templateService);

                await sessionService.EnsureDefaultSessionAsync();

                const string rounds = "[{\"RoundIndex\":\"R0001\",\"Events\":[],\"PlayerInput\":\"前进\"}]";
                await sessionService.SaveActiveSessionStateAsync(rounds, roundVariantsJson: "{}", phase: "Interrupted", interruptedStep: 5);

                var active = await sessionService.GetActiveSessionAsync();
                Assert.NotNull(active);
                Assert.Equal("Interrupted", active.CurrentRoundPhase);
                Assert.Equal(5, active.InterruptedStep);
            }

            // 2. 新 context（模拟关 App 重开）：从 DB 读回，段位/断点段不靠猜、与落库一致。
            SqliteConnection.ClearAllPools();
            await using (var context = CreateContext(databasePath))
            {
                var diceEngine = new DiceEngine(new DiceCommandParser(), new CharacterAttributeProvider(context), new SequenceDiceRoller([50]));
                var saveSystem = new SaveSystem(context, diceEngine);
                var templateService = new ProtagonistTemplateService(context, saveSystem);
                var sessionService = new ChatSessionService(context, templateService);

                var reloaded = await sessionService.GetActiveSessionAsync();
                Assert.NotNull(reloaded);
                Assert.Equal("Interrupted", reloaded.CurrentRoundPhase);
                Assert.Equal(5, reloaded.InterruptedStep);
            }
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task SaveActiveSessionLeavesPhaseUnchangedWhenNotProvided()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var diceEngine = new DiceEngine(new DiceCommandParser(), new CharacterAttributeProvider(context), new SequenceDiceRoller([50]));
            var saveSystem = new SaveSystem(context, diceEngine);
            var templateService = new ProtagonistTemplateService(context, saveSystem);
            var sessionService = new ChatSessionService(context, templateService);

            await sessionService.EnsureDefaultSessionAsync();

            // 先落一个明确段位。
            await sessionService.SaveActiveSessionStateAsync("[]", roundVariantsJson: null, phase: "AwaitingPlayer", interruptedStep: 2);
            // 再用「不带段位」的旧重载落库——段位/断点段应保留不动。
            await sessionService.SaveActiveSessionStateAsync("[]");

            var active = await sessionService.GetActiveSessionAsync();
            Assert.NotNull(active);
            Assert.Equal("AwaitingPlayer", active.CurrentRoundPhase);
            Assert.Equal(2, active.InterruptedStep);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    private static Re0AgentDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<Re0AgentDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new Re0AgentDbContext(options);
    }

    private static string CreateTempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "re0agent-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteIfExists(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
