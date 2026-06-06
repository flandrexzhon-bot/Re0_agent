namespace Re0Agent.Core.Services.Settings;

public interface IRagService
{
    Task<RagContext> QueryAsync(RagQuery query, CancellationToken cancellationToken = default);
}
