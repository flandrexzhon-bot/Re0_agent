using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class AgentConfigResolver(Re0AgentDbContext dbContext)
{
    public async Task<AgentConfig?> FindConfigAsync(
        string agentType,
        string agentName,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.AgentConfig
            .AsNoTracking()
            .Where(config => config.Enabled == 1)
            .Where(config => config.AgentName == agentName || config.AgentType == agentType)
            .OrderByDescending(config => config.AgentName == agentName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static LlmOptions? ToLlmOptions(AgentConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        return new LlmOptions(
            config.ApiEndpoint,
            config.ApiKey,
            config.ModelName,
            config.Temperature,
            config.MaxTokens);
    }
}
