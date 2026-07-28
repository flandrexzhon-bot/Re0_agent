using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Settings;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Tests;

public sealed class ContinuousModeHardGateTests
{
    [Fact]
    public void PacingFixtures_AreExecutableAndWithinLabeledRanges()
    {
        var results = new PacingFixtureEvaluator(new PacingFeatureExtractor()).EvaluateAll();

        Assert.Equal(8, results.Count);
        Assert.All(results, result => Assert.True(result.Passed, $"{result.ScenarioName}: {string.Join(", ", result.Failures)}"));
        Assert.Contains(PacingBaselineScenarios.All, item => item.Labels.Dragging);
        Assert.Contains(PacingBaselineScenarios.All, item => item.Labels.Rushed);
        Assert.Contains(PacingBaselineScenarios.All, item => item.Labels.KnowledgeLeak);
        Assert.Contains(PacingBaselineScenarios.All, item => item.Labels.CharacterDrift);
    }

    [Fact]
    public void PacingComparison_SeparatesThreeRepeatableBaselines()
    {
        var plans = PacingBaselineScenarios.All.ToDictionary(
            scenario => scenario.Name,
            scenario => new BeatPlan(
                $"recorded:{scenario.Name}",
                "calibration",
                [PacingBaselineScenarios.Materialize(scenario)[^1].EventId],
                [],
                "fixture_scene",
                .3,
                scenario.Labels.Dragging ? "advance_short" : "hold",
                [],
                "recorded_full_director_fixture"),
            StringComparer.Ordinal);

        var report = new PacingComparisonService(new PacingFeatureExtractor()).CompareFixedFixtures(plans);

        Assert.False(report.Baselines.Single(item => item.Mode == PacingBaselineMode.UnpacedContinuous).Passed);
        Assert.True(report.Baselines.Single(item => item.Mode == PacingBaselineMode.DeterministicPacing).Passed);
        Assert.True(report.Baselines.Single(item => item.Mode == PacingBaselineMode.FullDirector).Passed);
    }

    [Fact]
    public async Task LegacyCleanup_RemovesGameStateButPreservesConfigurationAndImports()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        await db.Database.OpenConnectionAsync();
        db.AgentConfig.Add(new AgentConfig
        {
            AgentType = "Character", AgentName = "preserved-agent", ApiEndpoint = "https://example.invalid",
            ApiKey = "key", ModelName = "model", Enabled = 1
        });
        db.CharacterCardSources.Add(new CharacterCardSource
        {
            SourceKey = "preserved-card", Format = "test", Name = "Card", AlternateGreetings = "[]",
            Extensions = "{}", CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        db.ChatSessions.Add(new ChatSession
        {
            SessionName = "legacy", IsActive = 1, CreatedAt = DateTimeOffset.UtcNow.ToString("O"), GameMode = "RP"
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE chat_sessions ADD COLUMN current_round_phase TEXT;");

        await DatabaseInitializer.InitializeAsync(db);

        Assert.Empty(await db.ChatSessions.AsNoTracking().ToListAsync());
        Assert.True(await db.AgentConfig.AnyAsync(item => item.AgentName == "preserved-agent"));
        Assert.True(await db.CharacterCardSources.AnyAsync(item => item.SourceKey == "preserved-card"));
        var columns = await ReadColumnsAsync(db, "chat_sessions");
        Assert.Contains("game_mode", columns);
        Assert.DoesNotContain("current_round_phase", columns);
    }

    [Fact]
    public async Task FeatureHashEmbedding_IsStableAndNormalized()
    {
        var provider = new FeatureHashMemoryEmbeddingProvider();
        var first = await provider.EmbedAsync("艾米莉亚 记得 王都");
        var second = await provider.EmbedAsync("艾米莉亚 记得 王都");

        Assert.Equal(first, second);
        Assert.Equal(256, first.Length);
        Assert.InRange(Math.Sqrt(first.Sum(value => value * value)), .999, 1.001);
    }

    [Fact]
    public async Task Charx_RequiresOneRootCardAndFirstMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        var importer = new SillyTavernImporter(db);
        var invalid = CreateCharx("{\"data\":{\"name\":\"Test\"}}");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportCharacterCardCharxAsync("test", invalid));

        Assert.Contains("first_mes", error.Message);
    }

    [Fact]
    public async Task Charx_InitializesIndependentCharacterAgency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var importer = new SillyTavernImporter(db);
        var card = CreateCharx("{\"data\":{\"name\":\"Imported\",\"description\":\"A traveler\",\"first_mes\":\"Hello\"}}");

        var imported = await importer.ImportCharacterCardCharxAsync("valid", card);

        var npc = await db.ImportantNpcs.SingleAsync(item => item.Name == imported.Name);
        Assert.True(await db.CharacterAgencyStates.AnyAsync(item => item.CharacterId == $"npc:{npc.RowId}" && item.Status == "Active"));
        Assert.True(await db.AgentConfig.AnyAsync(item => item.AgentName == imported.Name && item.AgentType == "Character"));
    }

    [Fact]
    public async Task RevealQueue_RebuildsCorruptedCacheFromCommittedCursors()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        db.ChatSessions.Add(new ChatSession { SessionId = 1, SessionName = "test", IsActive = 1, CreatedAt = now, GameMode = "RP", CurrentBranchId = "main", WorldClockAnchor = now });
        await db.SaveChangesAsync();
        db.TimelineBranches.Add(new TimelineBranch { BranchId = "main", SessionId = 1, BranchReason = "test", CreatedAt = now });
        db.WorldRuntimeStates.Add(new WorldRuntimeState { SessionId = 1, BudgetWindowStartedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        db.TimelineEvents.AddRange(
            Event("source", 1, now),
            Event("reveal", 2, now, "RevealCommitted", "[\"source\"]"));
        await db.SaveChangesAsync();
        db.RevealQueue.Add(new RevealQueueItem
        {
            SessionId = 1, EventId = "source", Status = "Pending", OccurredWorldTime = now,
            Importance = .5, AllowedVisibilityScope = "[]", LatestRevealWorldTime = now,
            CoalescedEventIds = "[]", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var writer = CreateEventWriter(db);
        var queue = new RevealQueueService(db, writer);

        await queue.RebuildStatusesFromEventsAsync(1);

        Assert.Equal("Revealed", (await db.RevealQueue.SingleAsync()).Status);
        Assert.True((await new ContinuousModeDiagnosticsService(db).EvaluateAsync(1)).Passed);
    }

    [Fact]
    public async Task CheckpointSavePointAndBranchReplay_AreDeterministic()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        db.ChatSessions.Add(new ChatSession { SessionId = 1, SessionName = "replay", IsActive = 1, CreatedAt = now, GameMode = "RP", CurrentBranchId = "root", WorldClockAnchor = now });
        await db.SaveChangesAsync();
        db.TimelineBranches.Add(new TimelineBranch { BranchId = "root", SessionId = 1, BranchReason = "root", CreatedAt = now });
        await db.SaveChangesAsync();
        var writer = CreateEventWriter(db);
        var initial = await writer.CommitAsync(1, "InitialProjection", "{}", stateChangeSet: SceneInsert("initial", now), triggerCause: "test");
        var ownerObservers = new DirectObserversResult(["npc:1"], new Dictionary<string, string> { ["npc:1"] = "same_scene" }, [], true);
        var rootUpdate = await writer.CommitAsync(1, "RuntimeControl", "{}", stateChangeSet: SceneUpdate("root"), observers: ownerObservers, triggerCause: "test");
        db.CharacterMemory.Add(new CharacterMemory
        {
            RowId = 1, OwnerCharacterId = "npc:1", SourceEventId = rootUpdate.EventId, WorldTime = now,
            WorldEpoch = 1, ObservationChannel = "same_scene", Confidence = "确知",
            VisibilityScope = "{\"direct\":[\"npc:1\"]}", MemoryType = "event", RetainOnRewind = 1,
            MemoryText = "retained", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var checkpoint = new ProjectionCheckpointService(db);
        var replayer = new ProjectionReplayer(db, new ProjectionCommandApplier(db), checkpoint);
        var savePoint = new SavePoint { BranchId = "root", EventId = initial.EventId, WorldEpoch = 1, TriggerReason = "test", CreatedAt = now };

        await replayer.ReplayToSavePointAsync(savePoint, ["npc:1"]);

        Assert.Equal("initial", (await db.SceneStates.SingleAsync()).Summary);
        Assert.True(await db.CharacterMemory.AnyAsync(item => item.OwnerCharacterId == "npc:1" && item.RetainOnRewind == 1));

        await replayer.ReplayToEventAsync("root", rootUpdate.EventId);
        db.TimelineBranches.Add(new TimelineBranch { BranchId = "child", SessionId = 1, ParentBranchId = "root", ParentEventId = rootUpdate.EventId, BranchReason = "branch", CreatedAt = now });
        await db.SaveChangesAsync();
        var session = await db.ChatSessions.SingleAsync();
        session.CurrentBranchId = "child";
        await db.SaveChangesAsync();
        var childUpdate = await writer.CommitAsync(1, "RuntimeControl", "{}", stateChangeSet: SceneUpdate("child"), observers: ownerObservers, triggerCause: "test");
        (await db.SceneStates.SingleAsync()).Summary = "corrupted";
        await db.SaveChangesAsync();

        await replayer.ReplayToEventAsync("child", childUpdate.EventId);

        Assert.Equal("child", (await db.SceneStates.AsNoTracking().SingleAsync()).Summary);
    }

    [Fact]
    public async Task DirectorPulse_IsPersistedShadowedAndConsumedOnNextTick()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await SeedSessionAsync(db, now, "RP");
        db.ProtagonistInfo.Add(Protagonist("scene"));
        await db.SaveChangesAsync();
        var writer = CreateEventWriter(db);
        var observers = new DirectObserversResult(["protagonist:1"], new Dictionary<string, string> { ["protagonist:1"] = "same_scene" }, [], true);
        await writer.CommitAsync(1, "CharacterAction", "visible", sceneId: "scene", observers: observers, triggerCause: "test");
        var director = new SceneDirector(
            db,
            new AgentConfigResolver(db),
            new OpenAiCompatibleLlmClient(new HttpClient()),
            new PaceGovernor(db, new PacingFeatureExtractor()),
            writer,
            new WorldAffordanceValidator(db));

        Assert.True(await new DirectorPulseProcessor(db, director).ProcessNextAsync(1));
        var pulse = await db.DirectorPulses.SingleAsync();
        Assert.Equal("Shadowed", pulse.Status);
        Assert.NotNull(pulse.BenefitScore);
        pulse.IsShadow = 0;
        pulse.Status = "Completed";
        pulse.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        await db.SaveChangesAsync();

        var plan = await new DirectorPulseConsumer(db).ConsumeReadyAsync(1, 0, CancellationToken.None);

        Assert.NotNull(plan);
        await db.Entry(pulse).ReloadAsync();
        Assert.Equal("Consumed", pulse.Status);
    }

    [Fact]
    public async Task OffscreenPromotion_UsesRegionSceneAndCommittedFact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await SeedSessionAsync(db, now, "Theater");
        db.SceneStates.AddRange(
            new SceneState { SceneId = "foreground", RegionId = "region", IsForeground = 1, Summary = "{}", LastWorldTime = now },
            new SceneState { SceneId = "nearby", RegionId = "region", IsForeground = 0, Summary = "{}", LastWorldTime = now });
        db.ImportantNpcs.Add(Npc(1, "nearby"));
        db.CharacterAgencyStates.Add(new CharacterAgencyState { CharacterId = "npc:1", SceneId = "nearby", Fidelity = "distant", Status = "Active", NextActionWorldTime = now });
        await db.SaveChangesAsync();
        var offscreen = Event("offscreen", 1, now, "DistantWorldAdvance");
        offscreen.ActorId = "npc:1";
        offscreen.SceneId = "nearby";
        db.TimelineEvents.Add(offscreen);
        await db.SaveChangesAsync();
        var writer = CreateEventWriter(db);
        var service = new OffscreenSimulationService(db, writer, new ObservabilityComputer(db));

        await service.PromoteForForegroundAsync(1, "foreground", DateTimeOffset.Parse(now));
        Assert.Equal("regional", (await db.CharacterAgencyStates.SingleAsync()).Fidelity);
        var promotion = await db.TimelineEvents.AsNoTracking().SingleAsync(item => item.EventType == "OffscreenFidelityPromoted");
        Assert.Equal("offscreen", promotion.CausalParentEventId);

        var agency = await db.CharacterAgencyStates.SingleAsync();
        agency.SceneId = "foreground";
        await db.SaveChangesAsync();
        await service.PromoteForForegroundAsync(1, "foreground", DateTimeOffset.Parse(now));
        Assert.Equal("foreground", agency.Fidelity);
    }

    [Fact]
    public async Task PlayerDirection_CompletesOnlyAfterTargetSceneIsRendered()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await SeedSessionAsync(db, now, "Theater");
        db.SceneStates.Add(new SceneState { SceneId = "target", RegionId = "region", Summary = "{}", LastWorldTime = now });
        await db.SaveChangesAsync();
        var writer = CreateEventWriter(db);
        var source = await writer.CommitAsync(1, "PlayerDirection", "场景：target", triggerCause: "test");
        db.PendingDirections.Add(new PendingDirection
        {
            SessionId = 1, SourceEventId = source.EventId, Content = "场景：target", Preconditions = "[]",
            EarliestWorldTime = now, Status = "Pending", CreatedAt = now
        });
        await db.SaveChangesAsync();
        var directions = new DirectionService(db, new DirectionDecomposer(), writer);

        Assert.Equal("Realizing", (await directions.RealizeNextAsync(1))?.Status);
        Assert.Equal("Realizing", (await directions.RealizeNextAsync(1))?.Status);
        Assert.Equal(1, await db.TimelineEvents.CountAsync(item => item.EventType == "PlayerDirectionRealizing"));
        Assert.Equal(1, await db.TimelineEvents.CountAsync(item => item.EventType == "DirectionSceneOpportunity"));
        await writer.CommitAsync(1, "KeplerNarration", "target shown", sceneId: "target",
            observers: new DirectObserversResult([], new Dictionary<string, string>(), [], true), triggerCause: "test");

        Assert.Equal("Completed", (await directions.RealizeNextAsync(1))?.Status);
        Assert.Equal("Completed", (await db.PendingDirections.SingleAsync()).Status);
    }

    [Fact]
    public async Task MemoryRetrieval_EnforcesEpochAndFallsBackWithoutVectors()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var now = DateTimeOffset.UtcNow.ToString("O");
        db.CharacterMemory.AddRange(
            Memory(1, "current", 2, 0, "apple", now),
            Memory(2, "retained", 1, 1, "loop", now),
            Memory(3, "hidden", 1, 0, "secret", now));
        await db.SaveChangesAsync();
        var provider = new FeatureHashMemoryEmbeddingProvider();
        var service = new MemoryRetrievalService(db);
        await service.RebuildAsync(provider);
        var query = await provider.EmbedAsync("apple");

        var vector = await service.RetrieveAsync("npc:1", 2, query, provider.ModelId, 8);
        var fallback = await service.RetrieveAsync("npc:1", 2, limit: 8);

        Assert.Contains(vector, item => item.MemoryText == "apple");
        Assert.DoesNotContain(vector, item => item.MemoryText == "secret");
        Assert.Equal(new[] { "loop", "apple" }, fallback.Select(item => item.MemoryText));
    }

    [Fact]
    public async Task OffscreenLongRun_KeepsAdvanceAndRevealQueuesBounded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await DatabaseInitializer.InitializeAsync(db);
        var start = DateTimeOffset.UtcNow;
        await SeedSessionAsync(db, start.ToString("O"), "Theater");
        db.CharacterAgencyStates.AddRange(Enumerable.Range(1, 40).Select(index => new CharacterAgencyState
        {
            CharacterId = $"npc:{index}", SceneId = "distant", CurrentGoal = "schedule",
            NextActionWorldTime = start.ToString("O"), Fidelity = "distant", Status = "Active"
        }));
        await db.SaveChangesAsync();
        var service = new OffscreenSimulationService(db, CreateEventWriter(db), new ObservabilityComputer(db));
        var maximumAdvance = 0;
        for (var tick = 0; tick < 20; tick++)
            maximumAdvance = Math.Max(maximumAdvance, await service.AdvanceDueAsync(1, start.AddMinutes(tick * 10)));

        Assert.InRange(maximumAdvance, 1, 8);
        Assert.True(await db.TimelineEvents.CountAsync(item => item.EventType == "DistantWorldAdvance") > 40);
        Assert.True(await db.RevealQueue.CountAsync(item => item.Status == "Pending" && item.MustReveal == 0) <= 128);
    }

    private static Re0AgentDbContext CreateContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<Re0AgentDbContext>().UseSqlite(connection).Options);

    private static async Task SeedSessionAsync(Re0AgentDbContext db, string now, string mode)
    {
        db.ChatSessions.Add(new ChatSession { SessionId = 1, SessionName = "test", IsActive = 1, CreatedAt = now, GameMode = mode, CurrentBranchId = "main", WorldClockAnchor = now });
        await db.SaveChangesAsync();
        db.TimelineBranches.Add(new TimelineBranch { BranchId = "main", SessionId = 1, BranchReason = "test", CreatedAt = now });
        await db.SaveChangesAsync();
    }

    private static EventSingleWriter CreateEventWriter(Re0AgentDbContext db) => new(
        db,
        new StateChangeSetValidator(),
        new ProjectionCommandApplier(db),
        new ProjectionCheckpointService(db),
        new DirectorPulseScheduler(db),
        new RevealQueueProjector(db));

    private static TimelineEvent Event(string id, long sequence, string now, string type = "CharacterAction", string cursors = "[]") => new()
    {
        EventId = id, BranchId = "main", Sequence = sequence, WorldEpoch = 1, SceneId = "scene",
        EventType = type, Content = "{}", DirectObservers = "[]", PotentialLearners = "[]",
        ObservabilityComputed = 1, VisibilityScope = "[]", RevealedEventCursors = cursors,
        Status = "Committed", RealCreatedAt = now, WorldTime = now
    };

    private static ProtagonistInfo Protagonist(string scene) => new()
    {
        RowId = 1, CharId = 1, Name = "Player", Gender = "unknown", Age = 18, Appearance = "unknown",
        IdentityText = "player", SelfStatus = "normal", LocationName = scene, BaseAttributes = "{}"
    };

    private static ImportantNpc Npc(int id, string scene) => new()
    {
        RowId = id, CharId = id, Name = $"Npc{id}", Gender = "unknown", Age = 18, BriefIntro = "npc",
        Appearance = "unknown", IdentityText = "npc", BaseAttributes = "{}", LocationName = scene,
        PastExperience = "none"
    };

    private static CharacterMemory Memory(int id, string source, int epoch, int retain, string text, string now) => new()
    {
        RowId = id, OwnerCharacterId = "npc:1", SourceEventId = source, WorldTime = now, WorldEpoch = epoch,
        ObservationChannel = "same_scene", Confidence = "确知", VisibilityScope = "{\"direct\":[\"npc:1\"]}",
        MemoryType = "event", RetainOnRewind = retain, MemoryText = text, CreatedAt = now
    };

    private static StateChangeSet SceneInsert(string summary, string now) => new(
    [
        new StateChangeCommand("scene-insert", "scene_states", "scene", "insert", null,
            new Dictionary<string, JsonElement>
            {
                ["region_id"] = JsonSerializer.SerializeToElement("region"),
                ["is_foreground"] = JsonSerializer.SerializeToElement(1),
                ["summary"] = JsonSerializer.SerializeToElement(summary),
                ["last_world_time"] = JsonSerializer.SerializeToElement(now)
            }, "test")
    ]);

    private static StateChangeSet SceneUpdate(string summary) => new(
    [
        new StateChangeCommand($"scene-update-{summary}", "scene_states", "scene", "update", null,
            new Dictionary<string, JsonElement> { ["summary"] = JsonSerializer.SerializeToElement(summary) }, "test")
    ]);

    private static byte[] CreateCharx(string json)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("card.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(json);
        }
        return stream.ToArray();
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(Re0AgentDbContext db, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(1));
        return result;
    }
}
