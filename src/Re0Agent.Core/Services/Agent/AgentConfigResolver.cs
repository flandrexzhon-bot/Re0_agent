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
        AgentConfig? resolvedConfig = null;
        string? targetPresetName = null;

        if (agentType == "GM")
        {
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "GM")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else if (agentType == "ChapterSwitch")
        {
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "ChapterSwitch")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(targetPresetName))
            {
                targetPresetName = await dbContext.ApiRoutings
                    .Where(r => r.RoutingKey == "GM")
                    .Select(r => r.PresetName)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }
        else if (agentType == "CharacterSub")
        {
            // 任务接力（角色调度）模型，未配置时回退到 GM 模型
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "CharacterSub")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(targetPresetName))
            {
                targetPresetName = await dbContext.ApiRoutings
                    .Where(r => r.RoutingKey == "GM")
                    .Select(r => r.PresetName)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }
        else if (agentType == "DiceGM")
        {
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "Dice")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(targetPresetName))
            {
                targetPresetName = await dbContext.ApiRoutings
                    .Where(r => r.RoutingKey == "GM")
                    .Select(r => r.PresetName)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }
        else if (agentType == "Form")
        {
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "Memory")
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else if (agentType == "Character")
        {
            // Try specific character binding first
            targetPresetName = await dbContext.ApiRoutings
                .Where(r => r.RoutingKey == "Character_" + agentName)
                .Select(r => r.PresetName)
                .FirstOrDefaultAsync(cancellationToken);

            // Fallback to NPC 执笔
            if (string.IsNullOrWhiteSpace(targetPresetName))
            {
                targetPresetName = await dbContext.ApiRoutings
                    .Where(r => r.RoutingKey == "NPC")
                    .Select(r => r.PresetName)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetPresetName))
        {
            resolvedConfig = await dbContext.AgentConfig
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentName == targetPresetName && c.Enabled == 1, cancellationToken);
        }

        // Fallback to old behavior: query agent_config table directly matching name or type
        if (resolvedConfig is null || string.IsNullOrWhiteSpace(resolvedConfig.ApiEndpoint) || string.IsNullOrWhiteSpace(resolvedConfig.ApiKey))
        {
            var fallback = await dbContext.AgentConfig
                .AsNoTracking()
                .Where(c => c.Enabled == 1)
                .Where(c => c.AgentName == agentName || c.AgentType == agentType)
                .OrderByDescending(c => c.AgentName == agentName)
                .FirstOrDefaultAsync(cancellationToken);

            if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.ApiEndpoint) && !string.IsNullOrWhiteSpace(fallback.ApiKey))
            {
                resolvedConfig = fallback;
            }
        }

        // Ultimate fallback: return first enabled config that has both API Endpoint and API Key configured
        if (resolvedConfig is null || string.IsNullOrWhiteSpace(resolvedConfig.ApiEndpoint) || string.IsNullOrWhiteSpace(resolvedConfig.ApiKey))
        {
            var ultimateFallback = await dbContext.AgentConfig
                .AsNoTracking()
                .Where(c => c.Enabled == 1 && c.ApiEndpoint != null && c.ApiEndpoint != "" && c.ApiKey != null && c.ApiKey != "")
                .OrderBy(c => c.ConfigId)
                .FirstOrDefaultAsync(cancellationToken);

            if (ultimateFallback is not null)
            {
                resolvedConfig = ultimateFallback;
            }
        }

        return resolvedConfig;
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
            config.MaxTokens,
            config.MaxInputTokens,
            config.EnableThinking,
            config.ReasoningEffort);
    }
}
