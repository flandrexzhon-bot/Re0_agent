using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Re0Agent.Core.Database;

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

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
