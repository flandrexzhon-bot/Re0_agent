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
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddScoped<OpenAiCompatibleLlmClient>();
        builder.Services.AddScoped<ILlmClient, AgentLlmClient>();
        builder.Services.AddSingleton<ChapterVariantRenderer>();
        builder.Services.AddSingleton<IRagService, BlackTeaRagService>();
        builder.Services.AddSingleton<IDiceRoller, RandomDiceRoller>();
        builder.Services.AddScoped<DiceCommandParser>();
        builder.Services.AddScoped<CharacterAttributeProvider>();
        builder.Services.AddScoped<DiceEngine>();
        builder.Services.AddScoped<AgentConfigResolver>();
        builder.Services.AddScoped<PromptComposer>();
        builder.Services.AddScoped<GmAgent>();
        builder.Services.AddScoped<CharacterSubAgent>();
        builder.Services.AddScoped<CharacterAgentService>();
        builder.Services.AddScoped<FormAgent>();
        builder.Services.AddScoped<SqlSafetyValidator>();
        builder.Services.AddScoped<FormAgentSqlExecutor>();
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
