using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        var databasePath = Path.Combine(FileSystem.AppDataDirectory, "re0agent.db");
        builder.Services.AddSingleton(new DatabaseLocation(databasePath));
        builder.Services.AddDbContext<Re0AgentDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        builder.Services.AddSingleton<HttpClient>(_ => new HttpClient
        {
            // 提高 LLM 请求超时上限：填表/大上下文请求常超过默认 100 秒。
            Timeout = TimeSpan.FromMinutes(10)
        });
        builder.Services.AddScoped<OpenAiCompatibleLlmClient>();
        builder.Services.AddScoped<ILlmClient, AgentLlmClient>();
        builder.Services.AddSingleton<IRagService, BlackTeaRagService>();
        builder.Services.AddSingleton<IDiceRoller, RandomDiceRoller>();
        builder.Services.AddScoped<DiceCommandParser>();
        builder.Services.AddScoped<CharacterAttributeProvider>();
        builder.Services.AddScoped<DiceEngine>();
        builder.Services.AddScoped<CombatResolver>();
        builder.Services.AddScoped<AgentConfigResolver>();
        builder.Services.AddScoped<PromptComposer>();
        builder.Services.AddScoped<CharacterNameResolver>();
        builder.Services.AddSingleton<CharacterIdRegistry>();
        builder.Services.AddScoped<CharacterAgentService>();
        builder.Services.AddScoped<FormAgent>();
        builder.Services.AddScoped<StateChangeSetValidator>();
        builder.Services.AddScoped<ProjectionCommandApplier>();
        builder.Services.AddScoped<ProjectionReplayer>();
        builder.Services.AddScoped<ProjectionCheckpointService>();
        builder.Services.AddScoped<EventSingleWriter>();
        builder.Services.AddScoped<ObservabilityComputer>();
        builder.Services.AddScoped<WorldClockService>();
        builder.Services.AddScoped<WorldTickGovernor>();
        builder.Services.AddScoped<PaceGovernor>();
        builder.Services.AddScoped<PacingFeatureExtractor>();
        builder.Services.AddScoped<SceneDirector>();
        builder.Services.AddScoped<KeplerAgent>();
        builder.Services.AddScoped<RuleResolver>();
        builder.Services.AddScoped<StateProjectionService>();
        builder.Services.AddScoped<RevealQueueService>();
        builder.Services.AddScoped<DirectionDecomposer>();
        builder.Services.AddScoped<DirectionService>();
        builder.Services.AddScoped<SillyTavernImporter>();
        builder.Services.AddScoped<ImportedGreetingService>();
        builder.Services.AddScoped<InitialSceneProposalService>();
        builder.Services.AddScoped<RuntimeLorebookService>();
        builder.Services.AddScoped<SillyTavernExporter>();
        builder.Services.AddScoped<DirectorReflectionService>();
        builder.Services.AddScoped<WorldCandidateBuilder>();
        builder.Services.AddSingleton<WorldCancellationRegistry>();
        builder.Services.AddScoped<GenerationInterruptionService>();
        builder.Services.AddScoped<OffscreenSimulationService>();
        builder.Services.AddScoped<MemoryRetrievalService>();
        builder.Services.AddScoped<InformationGate>();
        builder.Services.AddScoped<WorldAffordanceValidator>();
        builder.Services.AddScoped<SaveSystem>();
        builder.Services.AddScoped<ProtagonistTemplateService>();
        builder.Services.AddScoped<ChatSessionService>();
        builder.Services.AddScoped<AgentOrchestrator>();
        builder.Services.AddSingleton<GameProgressService>();
        builder.Services.AddSingleton<LlmLogService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
